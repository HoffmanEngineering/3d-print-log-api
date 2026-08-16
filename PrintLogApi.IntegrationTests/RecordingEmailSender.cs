using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PrintLogApi.Services;

namespace PrintLogApi.IntegrationTests
{
    /// <summary>
    /// Captures the emails the app tried to send, so tests can assert on the notification itself
    /// rather than only on the row that triggered it.
    /// <para>
    /// Registered as a singleton and shared by every test in a fixture, so assert by matching a
    /// marker unique to the test rather than by counting everything sent.
    /// </para>
    /// </summary>
    public class RecordingEmailSender : IEmailSender
    {
        public sealed record SentEmail(string To, string Subject, string Body);

        private readonly ConcurrentQueue<SentEmail> _sent = new();

        /// <summary>When set, every send throws — for testing that a failed notification is survivable.</summary>
        public bool ThrowOnSend { get; set; }

        /// <summary>
        /// When set, every send throws <see cref="OperationCanceledException"/> — the shape a client
        /// disconnect or an HTTP timeout arrives in, which a plain exception test does not cover.
        /// </summary>
        public bool ThrowCancelledOnSend { get; set; }

        public IReadOnlyList<SentEmail> Sent => _sent.ToArray();

        /// <summary>The emails whose body contains <paramref name="marker"/>.</summary>
        public IReadOnlyList<SentEmail> Matching(string marker) =>
            _sent.Where(e => e.Body != null && e.Body.Contains(marker, StringComparison.Ordinal)).ToArray();

        public Task SendEmailAsync(string email, string subject, string message)
        {
            if (ThrowCancelledOnSend)
            {
                throw new OperationCanceledException("Simulated cancellation during send.");
            }
            if (ThrowOnSend)
            {
                throw new InvalidOperationException("Simulated SMTP failure.");
            }
            _sent.Enqueue(new SentEmail(email, subject, message));
            return Task.CompletedTask;
        }
    }
}
