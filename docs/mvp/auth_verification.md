# Auth Verification

Date: 2026-07-10

## Manual checks

- Anonymous `GET /` returns `302` challenge to Azure B2C.
- Anonymous `POST /Archive/DownloadArchives` returns `302` challenge and does not execute the mutation action.
- `/health/live` returns `200 Healthy` without authentication.
- `/health/ready` returns `200 Healthy` without authentication.
- Viewer login works and shows `Role: Viewer`.
- Operator login via B2C `extension_DataManagerRole=Operator` (local email/password account `devalextest@gmail.com`) works and shows `Role: Operator`; `POST /Archive/TriggerSymbolUpdate` succeeds; `/hangfire` returns `403`.
- Admin login via B2C `extension_DataManagerRole=Admin` works and shows `Role: Admin`.
- Hangfire dashboard is available for Admin; previous Viewer `403` is gone for Admin.

## B2C role claim setup

DataManager reads the role from a B2C custom attribute:

- Attribute name in Azure B2C: `DataManagerRole` (custom attribute on the user flow / directory extension).
- In the OIDC token this arrives as `extension_DataManagerRole` (single role) or `extension_DataManagerRoles` (comma/semicolon/space-separated multiple roles).
- Accepted values: `Viewer`, `Operator`, `Admin` (case-insensitive).
- If the claim is absent or empty, the authenticated user gets `Viewer` by default (`IdentityProviderRoleClaimsTransformation`).
- Any unrecognized value in the claim is ignored (not mapped to a role).

Example decoded token claims (no real user identifiers):

```json
{
  "sub": "00000000-0000-0000-0000-000000000000",
  "extension_DataManagerRole": "Admin"
}
```

```json
{
  "sub": "11111111-1111-1111-1111-111111111111"
}
```

The second example has no role claim, so the user is authorized as `Viewer`.

## Automated checks

Added `BinanceDataCollector.DataManager.Tests` with focused auth tests:

- authenticated users without an explicit role get `Viewer`;
- B2C `*_DataManagerRole` extension claims map to ASP.NET roles;
- multiple provider roles are split and normalized;
- anonymous users do not get default roles;
- read-only controllers require `Viewer` policy;
- Archive mutation actions require `Operator` policy and `POST`;
- policy evaluation behaves per role (`PolicyAuthorizationTests`): a principal built from the real
  `IdentityProviderRoleClaimsTransformation` and evaluated by a real `IAuthorizationService` —
  - `Operator` claim -> passes `Viewer` and `Operator` policies, denied `Admin` policy;
  - no claim / `Viewer` -> passes `Viewer` only;
  - `Admin` -> passes all three;
  - anonymous -> denied `Operator`.

The `Operator` role is verified both at the policy-behavior level (`PolicyAuthorizationTests`)
and end-to-end via B2C browser login (see Manual checks above).

Current full baseline:

```text
dotnet test BinanceDataCollector.sln --no-restore
Passed: 20, Failed: 0, Skipped: 2
```

## Skipped pseudo-tests

Two old tests are explicitly skipped because they were not executable tests:

- `FeatureCalculatorWorkerTests.DoWorkAsync_WhenKlinesExist_CallsUpsertFeatures` - `Act` is commented out after worker constructor/workflow changes.
- `GapFillingTests.HistoricalAuditor_ShouldFindAndFill_SingleTradeIdGap` - uses stale `schema.sql` and placeholder SQL statements.

They should be rewritten later as real worker orchestration and Testcontainers repository tests.
