/**
 * Auth0 post-login Action: "Add email claims to API token".
 *
 * The API stores each user's address so it can send email (see docs/email-runbook.md). It reads
 * the address from these two claims on every authenticated request and syncs Users.Email when
 * they change. Custom claims must be namespaced or Auth0 silently drops them.
 *
 * Deploy: Auth0 dashboard > Actions > Library > Create Action (Login / Post Login, Node 22),
 * paste this file, Deploy, then add it to the Login flow. Existing sessions pick the claims up
 * on their next token refresh; until then the API leaves the stored address untouched.
 */
exports.onExecutePostLogin = async (event, api) => {
  if (!event.user.email) {
    return;
  }

  api.accessToken.setCustomClaim('https://3dprintlog.com/email', event.user.email);
  api.accessToken.setCustomClaim('https://3dprintlog.com/email_verified', event.user.email_verified === true);
};
