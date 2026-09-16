# Cross-end checks

See `D:\DWGC2E\docs\CROSS_END_REVIEW_PLAN_20260916.md` for findings, rollout gates and commands. Section 8 records the latest deployed batch; earlier sections are historical evidence.

- `cross-end-checks.test.mjs`: acceptance tests for the read-only review tool.
- `known-gaps.test.mjs`: G01 and H01 are **positive acceptance**: APP glossary uploads preserve web IDs/notes, web deletion works, and billed translations appear in web history.
- `cloud-browser.cjs`: actual browser tests against actual Worker handlers with isolated SQLite. Covers glossary save/reload/APP reads, revision conflicts, lost responses, explicit local migration, read recovery, CSV validation, and history pagination/detail. No production account writes.
- Tests use isolated databases and a mocked translation provider. They never use production credentials, send mail, or create payments.

Run from `D:\DWGC2E`:

```powershell
node --experimental-strip-types --test tests/Integration/*.test.mjs
$env:PLAYWRIGHT_MODULE='C:\Users\GQL\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\node_modules\playwright'
node --experimental-strip-types --test tests/Integration/cloud-browser.cjs
# Optional: test the exact isolated production candidate instead of the website workspace:
$env:WEBSITE_TEST_ROOT='D:\DWGC2E_Baselines\cloud-history-20260916-081820\site'
node --experimental-strip-types --test tests/Integration/cloud-browser.cjs
Remove-Item Env:WEBSITE_TEST_ROOT
```

The cloud-browser suite requires the existing Edge browser and Worker test dependencies. It tests account behavior locally, not production connectivity. A successful proxy probe does not establish that APP direct Worker connectivity works.

### Production-shell session lifecycle (2026-09-16)

`WebsiteProductionSessionLifecycle.cjs` exercises the isolated production-based website (not the newer, unshipped workspace shell). Set `SITE_ROOT` to its `public` directory and `PLAYWRIGHT_MODULE` to the installed Playwright module, then run `node --test tests/Integration/WebsiteProductionSessionLifecycle.cjs` from the repository root. Uses installed Edge headless; serves the candidate's actual CSP. All API traffic is intercepted, external traffic is blocked, and sessions/accounts/payments are fixtures only.

66 checks cover six account-related pages × removal/replacement/expiry × focus/pageshow/storage, idle expiry, delayed history success/401 after replacement, 320/390/1280px history pagination/error retry/legacy records/search/unsafe HTML and URL handling, and unconfirmed/confirmed logout. The production-based candidate and evidence currently live under `artifacts/cross-end-completion-20260916/web-lifecycle`. This does not prove real account synchronization, real payment, physical devices, or real email delivery.

The suite now has 74 cases. Eight additional cases cover initial and subsequent network failure/retry in history, keyboard-only search/row refresh at three viewport widths, duplicate list request suppression, and profile/device transport failure recovery. Each case has a 15-second timeout. `LOCAL_ASSET_ROUTES=1` optionally serves the exact candidate static bytes and CSP through Playwright response interception to avoid Windows loopback `ERR_ADDRESS_IN_USE`; API fixtures remain the same. Record this mode separately: it verifies browser behavior/CSP but is not a live HTTP delivery test. Default mode still uses the local HTTP server.

### Billing browser recovery extension (2026-09-16)

`WebsiteProductionSessionLifecycle.cjs` now has 80 cases. Six payment-page fixture cases cover lost checkout responses with and without reload (the exact idempotency key and request body must survive), offline confirmation followed by paid/entitlement recovery without creating an order, expired/unknown QR suppression, and an old order response arriving after a different order is selected. The six new cases pass with the default local HTTP server, and the full 80 pass with `LOCAL_ASSET_ROUTES=1`. Evidence: `artifacts/cross-end-completion-20260916/billing-interaction`. API responses, accounts, QR contents, and payment states are fixtures; these results do not establish production callbacks, financial reconciliation, real payments, or physical-device behavior.

### Server-side session revocation browser regression (2026-09-16)

`cloud-browser.cjs` now has 22 cases, including six server-side revocation cases. Account, profile, devices, history and terminology pages consume an actual Worker-handler 401 after server logout; revoked glossary writes cannot change stored entries. APP/other-user sessions remain usable. Fixture credentials are seeded once per tab, never re-injected after redirects. These use Edge, local HTTP and an isolated SQLite adapter, not production credentials or real D1/workerd browser connectivity.

Full regression: 22/22 passed against `artifacts/cross-end-completion-20260916/web-lifecycle/site/public`; evidence: `artifacts/cross-end-completion-20260916/server-revocation-browser/full-browser.log`.

The suite now has 25 cases. Three additional billing cases revoke the server session before viewing an order, confirming payment status, or hiding the order. Each verifies actual 401 handling, credential/order/QR/purchase-control cleanup, an unaffected APP session, and unchanged orders, subscriptions, payment events, settlements and hidden-order records. Only isolated seeded non-payable orders are used. Targeted 3/3 and full 25/25 passed with default local HTTP; see `server-revocation-browser/billing-targeted.log` and `full-browser-25.log` under the evidence root above. No production payment acceptance is implied.
