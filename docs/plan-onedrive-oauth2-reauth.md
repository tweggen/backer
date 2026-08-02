# OneDrive OAuth2 Re-Auth — Analysis & Remediation Plan

Status: **Phase 1 committed 2026-08-01 (`4314499`). Phase 2 implemented 2026-08-02 (uncommitted) — see `docs/plan-phase2-gates.md` for gates, acceptance criteria and the verification log. Phase 3 pending.** Phase 0 (rotating the Azure client secret) is the operator's action and is not tracked here. Root cause CONFIRMED (2026-08-01): the Azure app-registration **client secret had expired** (~2026-07-16). This broke both token refresh and re-authentication (`invalid_client` on every token exchange), and a stack of code defects made the failure completely silent in the UI and logs.

## Context

Re-authenticating the OneDrive storage via OAuth2 on the live system (api.essentialvault.de) failed: `triggerOAuth2` returned 200 but the UI kept showing "Not authenticated". The previously stored OneDrive token expired 2026-07-16 04:49:57 UTC; the last code change was 2026-03-21 — hence environmental trigger (confirmed: expired client secret). Remedy for the trigger itself: create a new secret in the Azure portal and update `OAuth2:Providers:onedrive` config (user-secrets id `dadab942-14e1-4dd9-b8b4-8877ad2fc9f5` locally; server config on live).

### Root-cause chain (verified in code)

1. **Re-auth flow**: UI badge/button is purely `Storage.AccessToken` from the DB (`frontend/Poe/Components/Pages/BackerPages/Storages.razor:137-156`). `triggerOAuth2` (`application/Hannibal/Services/HannibalService.cs:87-113`) creates a 10-min single-use state row and redirects the browser to Microsoft with `redirect_uri = http://localhost:53682/` (hardcoded, `application/Hannibal/OAuth2ClientFactory.cs:39`) — served by the **local BackerAgent** (`BackerAgent/Program.cs:347-403`). While the agent is stopped, re-auth cannot complete at all.
2. **Silent exchange failure**: the code-for-token exchange (`HannibalService.ProcessOAuth2ResultAsync`, `HannibalService.cs:166-215`) swallows ALL exceptions into `Error = "Unable to read user info"` without logging; the agent callback (`BackerAgent/Program.cs:347-403`) never checks `result.Error`, has an **empty catch**, and redirects back to the Storages page regardless. The real failure reason (here: `invalid_client`) is discarded.
3. **Cross-site return leg**: the `access_token` cookie is `SameSite=Strict` (`Login.razor:95-100`, `Tools/HttpContextTokenProvider.cs:17-22`), so the browser omits it on the cross-site return navigation → `[AddTokenHandler] Token: <empty>` → 401 on `GET /users/-1`.
4. **Broken 401 handling**: `Storages.razor:698-719` — `GetUserAsync` swallows the 401 → returns null → `NavigateTo` throws `NavigationException` (by design in SSR) inside a catch-all that swallows it, and there is no `return` before `_rereadStorages()` (line 708, matching the observed log). `Jobs/Rules/Landscape/Account` were migrated to `AuthState.TriggerRedirectToLogin()`; **`Storages.razor` and `Endpoints.razor` were missed** (half-completed refactor).
5. **Dead broadcast**: `ProcessOAuth2ResultAsync` mutates the tracked entity then calls `UpdateStorageAsync(sto.Id, sto, …)`; inside (`application/Hannibal/Services/HannibalServiceStorages.cs:60-141`), the re-query returns the **same tracked instance**, so all change comparisons are self-comparisons → `credentialsChanged == false` → the `StorageReauthenticated` SignalR broadcast (`:131-138`) never fires for the OAuth path. (The token IS still persisted by `SaveChangesAsync` at `:125`.)
6. Additional latent bug: unguarded `ParseUserInfo` in the vendored fork (`external/OAuth2/OAuth2/Client/Impl/MicrosoftGraphClient.cs:171-194`) — `givenName`/`surname` may be absent on MSA accounts → NRE → same silent-failure path as (2).

### AUTHENTICATION.md audit (summary)

Doc from 2026-01-19; frontend login rewritten 01-24/29 → section 3 wrong: the JWT lives in an `access_token` **cookie** (not user claims); `HttpContextTokenProvider` reads cookies and implements `IStaticTokenProvider`; there is no "max 10 retries" in `AutoAuthHandler` (single 401-retry; also logs JWT cleartext, has dead never-assigned fields, and a retry bug with non-rewindable request content); `ConstantTokenProvider` is registered scoped, not app-lifetime. Missing topics: `FrontendHttpRedirectHandler`/`AuthState` 401 mechanism, `IdentityCookieHandler`, logout, cookie scheme config, Blazor authorize plumbing (`Storages`/`Endpoints`/`Jobs` are NOT `[Authorize]`-protected), unauthenticated SignalR hub, unauthenticated `DELETE /api/authb/v1/deleteUser`, stock `/api/auth/v1/*` MapIdentityApi surface, dead `HannibalServiceClient.SetAuthorizationHeader` (origin of the doc's wrong claims-based description), committed credentials (`BackerAgent/config.json`, Jwt key in appsettings).

### Test infrastructure audit (summary)

28 tests total, all pure unit (password obscurer, dependency graph, path overlap). Zero tests for JWT/OAuth2/providers. No mocking library, no `Mvc.Testing`, no EF test provider, no CI (`.github/workflows/` empty). `external/OAuth2` is a live-path submodule fork; its own NUnit tests (NSubstitute `IRequestFactory` pattern — usable precedent) are excluded from Backer.sln. Testability blockers: no DB harness; no API host harness; `OAuth2ClientFactory` is concrete with no seam (`HannibalService.cs:62` news it up); `OneDriveProvider.GetDriveInfoAsync` constructs `HttpClient` inline (line 58, makes a real Graph call); redirect URI hardcoded; no dry-run concept anywhere.

---

## Plan

### Phase 0 — Restore live operation (root-cause remedy)

1. Create a new client secret in Azure portal (App registrations → OneDrive app → Certificates & secrets). Note the new expiry date somewhere findable.
2. Update `OAuth2:Providers:onedrive:ClientSecret` in server config (and local user-secrets for dev).
3. Restart the stack, re-run the OAuth2 authenticate flow from the Storages page (local BackerAgent must be running for the `localhost:53682` callback).

### Phase 1 — Fix the silent-failure bugs

Each is small and independent:

1. **Surface OAuth errors** — `BackerAgent/Program.cs:347-403`: check `result.Error`, log it, and redirect with `?oauthError=...` appended to `AfterAuthUri`; replace the empty catch with logging. `HannibalService.cs:216-225`: log the exception (`_logger.LogError`). `Storages.razor`: read the `oauthError` query param and render it.
2. **SameSite fix** — change the `access_token` cookie to `SameSiteMode.Lax` in `Login.razor:95-100` and `HttpContextTokenProvider.cs:17-22` so the cross-site top-level return navigation carries the token.
3. **401 handling** — migrate `Storages.razor:698-719` and `Endpoints.razor:415-419` to the existing `AuthState.TriggerRedirectToLogin(); return;` pattern (copy from `Jobs.razor:163-168`).
4. **Dead broadcast** — in `HannibalServiceStorages.UpdateStorageAsync`, detect self-update (`ReferenceEquals(storage, updatedStorage)`) and treat OAuth-path token writes as `credentialsChanged = true` (or snapshot old values before mutation in `ProcessOAuth2ResultAsync`); this also fixes `_onOAuthDisconnect` never notifying agents (the `:81` guard skips empty tokens).
5. **Guard `ParseUserInfo`** — null-safe `givenName`/`surname` access in `external/OAuth2/OAuth2/Client/Impl/MicrosoftGraphClient.cs:171-194` (submodule fork — commit there and bump the pin).

### Phase 2 — Test harness (safe, no live backup data) — **DONE 2026-08-02**

Delivered: 36 tests → **104** (103 passing, 1 opt-in live check skipped) across
four projects. Details, acceptance criteria and evidence in
`docs/plan-phase2-gates.md`; how to run them in `docs/TESTING.md`.


1. **DB fixture**: xUnit `ICollectionFixture` in a new `tests/Hannibal.IntegrationTests` project — connects to local Postgres, creates a throwaway DB per run, runs `Database.Migrate()`, drops on dispose.
2. **API host**: add `Microsoft.AspNetCore.Mvc.Testing`; expose `public partial class Program` in `Api/Program.cs`; `WebApplicationFactory<Program>` wired to the fixture DB. Integration tests: `/api/authb/v1/token`, `triggerOAuth2` (state row created), `processOAuth2Result` (state validation, token persistence, broadcast fired — regression test for Phase 1 fix 4).
3. **Fake authorization server**: reuse the submodule's `IRequestFactory` substitution precedent (`external/OAuth2/OAuth2.Tests/Client/OAuth2ClientTests.cs`) — add a factory seam so `OAuth2ClientFactory`/`MicrosoftGraphClient` can be pointed at a stub token endpoint; test the full exchange including error paths (`invalid_client` — the exact failure that occurred — and missing `givenName`).
4. **Safe live-token check** (opt-in, env-var-gated): refactor `OneDriveProvider.GetDriveInfoAsync` to use `IHttpClientFactory`; add a harness that runs `EnsureTokensValidAsync` + read-only `GET /me/drive` against the real account — verifies credentials end-to-end without starting rclone or touching backup data.
5. **Unit tests** for `AutoAuthHandler` (401-retry, content-rebuffering bug) and `AddTokenHandler` via fake inner handlers; remove the token `Console.WriteLine` leaks while there.

### Phase 3 — Rewrite AUTHENTICATION.md

Correct sections 2/3 (cookie-based flow, real `HttpContextTokenProvider`, real `AutoAuthHandler` semantics, scoped `ConstantTokenProvider`); add the missing topics (AuthState/redirect handler, IdentityCookieHandler, logout, `/api/auth/v1/*` surface, SignalR hub auth gap, OAuth2 storage-auth architecture including the `localhost:53682` agent callback); add a Security-Issues section (unauthenticated deleteUser endpoint, committed secrets, token logging). Also fix `docs/STORAGE_REAUTH_IMPLEMENTATION.md`'s incorrect "No changes required" frontend claim.

## Verification

- Phase 1: `dotnet build Backer.sln`; run Api + Poe + BackerAgent locally against local Postgres with a scratch OneDrive `Storage` row; simulate a failing exchange (bogus secret) and confirm the error is now visible in UI + logs; confirm the post-OAuth return leg carries the cookie (no empty-token 401 on `/users/-1`) and the badge updates after successful auth without a manual reload.
- Phase 2: `dotnet test` runs green locally against local Postgres; the broadcast regression test fails before Phase 1 fix 4 and passes after.
- Phase 3: proofread the doc against the code references collected above.
