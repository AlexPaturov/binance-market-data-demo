# Auth Verification

Date: 2026-07-10

## Manual checks

- Anonymous `GET /` returns `302` challenge to Azure B2C.
- Anonymous `POST /Archive/DownloadArchives` returns `302` challenge and does not execute the mutation action.
- `/health/live` returns `200 Healthy` without authentication.
- `/health/ready` returns `200 Healthy` without authentication.
- Viewer login works and shows `Role: Viewer`.
- Admin login via B2C `DataManagerRole=Admin` works and shows `Role: Admin`.
- Hangfire dashboard is available for Admin; previous Viewer `403` is gone for Admin.

## Automated checks

Added `BinanceDataCollector.DataManager.Tests` with focused auth tests:

- authenticated users without an explicit role get `Viewer`;
- B2C `*_DataManagerRole` extension claims map to ASP.NET roles;
- multiple provider roles are split and normalized;
- anonymous users do not get default roles;
- read-only controllers require `Viewer` policy;
- Archive mutation actions require `Operator` policy and `POST`.

Current full baseline:

```text
dotnet test BinanceDataCollector.sln --no-restore
Passed: 15, Failed: 0, Skipped: 2
```

## Skipped pseudo-tests

Two old tests are explicitly skipped because they were not executable tests:

- `FeatureCalculatorWorkerTests.DoWorkAsync_WhenKlinesExist_CallsUpsertFeatures` - `Act` is commented out after worker constructor/workflow changes.
- `GapFillingTests.HistoricalAuditor_ShouldFindAndFill_SingleTradeIdGap` - uses stale `schema.sql` and placeholder SQL statements.

They should be rewritten later as real worker orchestration and Testcontainers repository tests.
