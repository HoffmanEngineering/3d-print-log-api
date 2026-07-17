using System;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PrintLogApi.Models;

namespace PrintLogApi.Services
{
    public class FeedbackService : IFeedbackService
    {
        private const string NotAvailable = "(not available)";

        private readonly PrintLogContext _context;
        private readonly IEmailSender _emailSender;
        private readonly IAuth0Service _auth0Service;
        private readonly TelemetryClient _telemetry;
        private readonly string _feedbackEmail;

        public FeedbackService(
            PrintLogContext context,
            IEmailSender emailSender,
            IAuth0Service auth0Service,
            TelemetryClient telemetry,
            IConfiguration config)
        {
            _context = context;
            _emailSender = emailSender;
            _auth0Service = auth0Service;
            _telemetry = telemetry;
            _feedbackEmail = config["FeedbackEmailAddress"];
        }

        public async Task<Feedback> CreateFeedback(
            long userId, Feedback.FeedbackType type, string email, string note, CancellationToken ct)
        {
            var feedback = new Feedback
            {
                Type = type,
                Email = email,
                Note = note,
                CreatedById = userId,
                UpdatedById = userId,
            };

            _context.Feedback.Add(feedback);
            await _context.SaveChangesAsync(ct);
            _telemetry.TrackEvent("FeedbackAdded");

            await NotifyBestEffort(feedback, userId, FeedbackSource.Website, ct);
            return feedback;
        }

        public async Task<Mcp.CreateFeedbackResult> CreateFeedbackForMcp(
            long userId, Feedback.FeedbackType type, string note, string idempotencyKey, CancellationToken ct)
        {
            const string toolName = "create_feedback";

            // Canonicalize ONCE, here, before both fingerprinting and persistence: the fingerprint
            // decides whether two calls are the same request, so anything normalized away must also
            // be normalized in what is stored. Never normalize inside the fingerprint instead.
            note = note?.Trim();

            var key = RequireIdempotencyKey(idempotencyKey);
            var fingerprint = Mcp.McpRequestFingerprint.ComputeCreateFeedback(type, note);

            var replay = await FindIdempotentFeedback(userId, toolName, key, fingerprint, ct);
            if (replay != null)
            {
                return replay;
            }

            var feedback = new Feedback
            {
                Type = type,
                // Never set from an agent: this column means "the address the user typed into the
                // website form", and an agent submits no form. The contact address for agent
                // feedback is resolved from Auth0 when the notification is composed.
                Email = null,
                Note = note,
                CreatedById = userId,
                UpdatedById = userId,
            };

            try
            {
                await CreateFeedbackWithIdempotencyRecord(feedback, userId, key, fingerprint, ct);
            }
            catch (DbUpdateException)
            {
                // Possible unique-index race: another identical call created the feedback first.
                // Clear the failed Added entities so the recovery query reads committed state only,
                // then replay the winner. No such record means the failure was something else
                // entirely — rethrow rather than reporting it as an idempotency problem.
                _context.ChangeTracker.Clear();
                var concurrent = await FindIdempotentFeedback(userId, toolName, key, fingerprint, ct);
                if (concurrent != null)
                {
                    return concurrent;
                }
                throw;
            }

            _telemetry.TrackEvent("FeedbackAdded");

            // After commit, and only on the create path: a replay must never re-notify.
            await NotifyBestEffort(feedback, userId, FeedbackSource.McpAgent, ct);

            // No cache invalidation: feedback is not part of any cached response. Every other write
            // tool invalidates because it changes data the read paths cache; this one does not.
            return new Mcp.CreateFeedbackResult(Describe(feedback), WasReplayed: false);
        }

        /// <summary>
        /// Creates the feedback and its idempotency record atomically. Lets DbUpdateException escape:
        /// only the caller can tell a lost unique-index race (replayable) from a genuine write
        /// failure (not), because only it knows the key and fingerprint to look the winner up with.
        /// </summary>
        private async Task CreateFeedbackWithIdempotencyRecord(
            Feedback feedback, long userId, string key, string fingerprint, CancellationToken ct)
        {
            // SqlServerRetryingExecutionStrategy forbids user-initiated transactions unless they run
            // inside an execution strategy, so the whole tx is the retriable unit.
            var strategy = _context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                using var tx = await _context.Database.BeginTransactionAsync(ct);
                _context.Feedback.Add(feedback);
                await _context.SaveChangesAsync(ct);

                _context.McpIdempotencyRecords.Add(
                    Mcp.McpIdempotencyRecordFactory.ForFeedback(userId, key, fingerprint, feedback.Id));
                await _context.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            });
        }

        private async Task<Mcp.CreateFeedbackResult> FindIdempotentFeedback(
            long userId, string toolName, string key, string fingerprint, CancellationToken ct)
        {
            var record = await _context.McpIdempotencyRecords.AsNoTracking()
                .FirstOrDefaultAsync(r => r.UserId == userId && r.ToolName == toolName && r.IdempotencyKey == key, ct);
            if (record == null)
            {
                return null;
            }

            // A key reused with a DIFFERENT payload is a caller bug, not a retry: replaying would
            // silently discard the new arguments. A null fingerprint is a legacy record with no
            // stored payload to compare, so it replays unconditionally.
            if (record.RequestFingerprint != null && record.RequestFingerprint != fingerprint)
            {
                throw Mcp.McpToolException.Conflict("This idempotency key was already used with different arguments.");
            }

            // Reads only its OWN target field. A record scoped to this tool with no CreatedFeedbackId
            // is dangling, whatever else it may carry. Ownership is re-checked in the predicate.
            var feedbackId = record.CreatedFeedbackId;
            var feedback = feedbackId.HasValue
                ? await _context.Feedback.AsNoTracking()
                    .FirstOrDefaultAsync(f => f.Id == feedbackId.Value && f.CreatedById == userId, ct)
                : null;
            if (feedback == null)
            {
                throw Mcp.McpToolException.NotFound("The prior result for this idempotency key no longer exists.");
            }

            return new Mcp.CreateFeedbackResult(Describe(feedback), WasReplayed: true);
        }

        /// <summary>
        /// Composes and sends the notification. Best-effort by design: the feedback row is already
        /// committed and is the source of truth, so a mail or Auth0 failure must not fail the call.
        /// Failing would not undo the row — it would only mislead the caller into reporting failure
        /// for feedback that was saved, and (on the MCP path) burn the idempotency key so the retry
        /// replays without ever notifying. The tracked events are the thing to alarm on.
        /// </summary>
        private async Task NotifyBestEffort(Feedback feedback, long userId, FeedbackSource source, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_feedbackEmail))
            {
                return;
            }

            try
            {
                var user = await _context.Users.AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == userId, ct);
                var accountEmail = await ResolveAccountEmail(user, ct);
                var body = BuildBody(feedback, user, userId, accountEmail, source);
                await _emailSender.SendEmailAsync(_feedbackEmail, "New 3D Print Log Feedback", body);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _telemetry.TrackEvent("FeedbackNotificationFailed");
                _telemetry.TrackException(ex);
            }
        }

        /// <summary>
        /// The account email from Auth0. Degrades to null rather than throwing: an Auth0 outage or a
        /// missing read:users grant must not cost us the notification entirely.
        /// </summary>
        private async Task<string> ResolveAccountEmail(User user, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(user?.OAuthUserId))
            {
                return null;
            }

            try
            {
                return await _auth0Service.GetUserEmail(user.OAuthUserId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _telemetry.TrackEvent("FeedbackAccountEmailLookupFailed");
                _telemetry.TrackException(ex);
                return null;
            }
        }

        private static string BuildBody(
            Feedback feedback, User user, long userId, string accountEmail, FeedbackSource source)
        {
            var displayName = Escape(user?.DisplayName) ?? NotAvailable;
            var submittedVia = source == FeedbackSource.McpAgent ? "MCP agent" : "Website";

            return $@"
By: {displayName} (User ID: {Escape(userId.ToString())}) <br>
Email (from Auth0): {Escape(accountEmail) ?? NotAvailable} <br>
Email (entered by user): {Escape(feedback.Email) ?? "(not present)"} <br>
Submitted via: {submittedVia} <br>
Type: {Enum.GetName(typeof(Feedback.FeedbackType), feedback.Type)} <br>
Feedback ID: {feedback.Id} <br>
<br>
Feedback: <br>
{Escape(feedback.Note)}
";
        }

        private static string Escape(string value) =>
            value == null ? null : SecurityElement.Escape(value);

        private static Mcp.FeedbackWriteResult Describe(Feedback f) =>
            new(f.Id, f.Type.ToString(), f.Note);

        private static string RequireIdempotencyKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw Mcp.McpToolException.InvalidArguments("idempotencyKey is required.");
            }
            // Trim BEFORE the length check: the trimmed value is what gets stored and compared, so
            // that is the value the limit applies to.
            var trimmed = key.Trim();
            Mcp.McpWriteValidation.RequireMaxLength(trimmed, 200, "idempotencyKey");
            return trimmed;
        }
    }
}
