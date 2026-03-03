# Reviewer.Console

Moderation and evidence workflow CLI for control-plane internal endpoints.

Default auth:

- Header: `X-Internal-Api-Key`
- Default key: `dev-internal-api-key`

Examples:

```powershell
dotnet run --project src/Reviewer.Console -- --backend http://localhost:5042 --internal-api-key dev-internal-api-key system-metrics
dotnet run --project src/Reviewer.Console -- --backend http://localhost:5042 --internal-api-key dev-internal-api-key list-evidence
dotnet run --project src/Reviewer.Console -- --backend http://localhost:5042 --internal-api-key dev-internal-api-key list-cases --status open
dotnet run --project src/Reviewer.Console -- --backend http://localhost:5042 --internal-api-key dev-internal-api-key create-case --evidence ev_x --match ms_x --account acc_x --reason rules_fire_cadence --priority high --by mod_1
dotnet run --project src/Reviewer.Console -- --backend http://localhost:5042 --internal-api-key dev-internal-api-key update-case --case case_x --status in_review --reviewer mod_2 --notes "review started"
dotnet run --project src/Reviewer.Console -- --backend http://localhost:5042 --internal-api-key dev-internal-api-key create-ban --account acc_x --scope queue --reason confirmed_cheat --evidence ev_x --duration-hours 24 --by mod_2
dotnet run --project src/Reviewer.Console -- --backend http://localhost:5042 --internal-api-key dev-internal-api-key list-bans --account acc_x --status active
dotnet run --project src/Reviewer.Console -- --backend http://localhost:5042 --internal-api-key dev-internal-api-key get-ban --ban ban_x
dotnet run --project src/Reviewer.Console -- --backend http://localhost:5042 --internal-api-key dev-internal-api-key update-ban --ban ban_x --status revoked --notes "manual revoke" --by mod_2
dotnet run --project src/Reviewer.Console -- --backend http://localhost:5042 --internal-api-key dev-internal-api-key create-appeal --ban ban_x --account acc_x --notes "requesting review"
dotnet run --project src/Reviewer.Console -- --backend http://localhost:5042 --internal-api-key dev-internal-api-key resolve-appeal --appeal appeal_x --status upheld --reviewer mod_3 --notes "evidence sufficient"
```
