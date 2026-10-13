# Independent sender and Paulatim mail branding

Implementation and validation record, 2026-10-12. All example identities are fictional. Real credentials, messages, snapshots and verification evidence remain private.

## Decision

Use an independent QQ mailbox to send to the approved Gmail recipient. The user authorized a real verification email, a controlled retry after authentication failure, and publication through local Git after successful verification. Publishing or staged verification does not by itself migrate installed scheduled tasks.

QQ SMTP requires a client authorization code rather than the QQ login password. [Tencent documentation](https://cloud.tencent.com/document/product/1270/55456) describes this authorization-code requirement. Outlook.com SMTP requires modern OAuth2 authentication according to [Microsoft's settings](https://support.microsoft.com/en-au/outlook/pop-imap-and-smtp-settings-for-outlook-com). Microsoft Graph is an alternative, with delegated Mail.Send support, but requires app registration and token lifecycle management; [sendMail](https://learn.microsoft.com/en-us/graph/api/user-sendmail?view=graph-rest-1.0) acceptance does not establish delivery. Choosing QQ reduces integration and maintenance work for this local Windows project; that is an engineering assessment, not a deliverability guarantee. No paid model API is used.

## Implemented

- New report subjects and sender display name use **Paulatim**. Existing daily/weekly receipt keys are unchanged. The legacy Gmail worker searches both Paulatim and WorkLog subjects; a verified complete historical report remains sent.
- Explicit `qq_smtp` transport uses `smtp.qq.com:587`, STARTTLS with normal certificate validation, the original CSV attachment, and the approved independent sender/recipient. There is no automatic Gmail fallback.
- The authorization code is entered in a local secure dialog and stored with Windows Credential Manager under the current user. JSON contains a credential reference, never the code. Setup requires normal mail to be disabled and uses Windows PowerShell 5.1.
- A separate durable SMTP ledger freezes the first payload, records intent before the external send and SMTP acceptance afterward. Accepted periods are reused without another send; a crash or ambiguous network outcome blocks automatic resend. An explicit SMTP 535 authentication rejection is safely retryable with bounded attempts. A locally generated Message-ID identifies the period; it does not make the server deduplicate mail.
- Legacy pending jobs prevent changing provider until reconciled. Existing confirmed receipts suppress the corresponding daily/weekly send even when its subject changes.
- Strict mailbox and legacy receipt-ID validation reject trailing control characters.

## Actual validation

| Check | Result | Evidence boundary |
| --- | --- | --- |
| Original regression checks | 54/54 passed on the final code | 22 sync, 28 dispatcher and 4 integration checks; synthetic CSV, simulated due times, isolated executables |
| SMTP fault suite | 18/18 passed | Fake sender only; no network, credentials, or real messages |
| Branding compatibility | 5/5 passed | Canonical subjects, old receipt keys, outgoing body and exact attachment bytes |
| Independent audit | 10/11 passed | Remaining synthetic Gmail-ID finding applies to the legacy CLI path |
| Native secure setup | Renewed code saved in the current user's Windows credential vault | The restricted runner initially refused vault writes; the authorized local setup succeeded with the required access. No code in JSON/Git |
| TLS/authentication | Renewed strict-TLS login succeeded with SMTP 235 | Authentication-only diagnostic submits no message; initial invalid credential was rejected with 535 and removed |
| Recipient readback | Passed: one matching verification message observed in Gmail INBOX | Seven checks: approved sender/recipient, Paulatim subject/display name, server Message-ID present, exact frozen HTML, byte-identical CSV attachment |
| Repeated verification invocation | Exit 0, idle, no send | Existing confirmed verification receipt suppresses sending; normal staged mail remains disabled |
| Installed automation migration | Not performed | Existing runtime and scheduled tasks have not been replaced |

The first real attempt was conservatively marked uncertain before numeric SMTP diagnostics were added. Its private ledger was archived. Before the explicitly user-approved single retry, Gmail was checked again, renewed authentication returned 235, and only the verification entry was restored to prepared with its original frozen payload and client identity. QQ accepted the retry; the actual received Gmail message was read as raw MIME and compared to the frozen HTML and CSV bytes. Existing formal-period receipts were retained. No installed task or source CSV was changed.

**Observed provider behavior:** QQ rewrote the supplied standard Message-ID. The local deterministic ID is an intent/acceptance identifier, not an observed Gmail receipt or a stable provider header. Private evidence therefore retains both IDs and the immutable Gmail ID. Reconciliation must search approved identities and exact period/subject, inspect body and attachment consistency, and refuse ambiguous matches; searching only the generated Message-ID would miss this received message. Received verification proves this delivery, not all future deliveries.

Reproduce offline checks from the repository root with Windows PowerShell 5.1:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tools\Test-MailBranding.ps1
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tools\Test-SmtpOffline.ps1
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tools\Review-Offline.ps1
```

The last command intentionally exits 1 while the legacy receipt finding remains. SMTP acceptance is not a Gmail inbox readback. Live iPhone propagation and a real shutdown across 08:00 are not covered by these offline tests.

## Controlled runtime migration

1. Verify the renewed QQ login and an actual received Gmail message, including sender, Paulatim subject, full body and exact original CSV attachment bytes. Keep proof private.
2. Review installed task ownership, executable paths and trigger/settings exports. Back up private configuration, valid mirror and both ledgers. Wait for an active sender to finish; do not kill an uncertain send.
3. Prepare tested native executables and unchanged source path in the intended runtime directory. Preserve all confirmed period receipts and reconcile any legacy pending job.
4. Obtain deployment authorization before changing installed tasks. Ensure only one active mail entry point. The generic renamed installers deliberately reject legacy tasks and are not a migration tool.
5. Enable normal QQ mail only after verification and the reviewed cutover. Check runtime paths, credential availability under the task user, the next due period and status. Preserve a rollback copy, but never restore stale receipt state or reactivate two senders.

This record does not claim production deployment. Remaining work includes controlled migration/rollback, recipient reconciliation tooling for ambiguous outcomes, legacy CLI trust and timeout issues, bounded private snapshot retention and health notifications. Category/Task processing changes remain unapproved; CSV parsing and iCloud input are unchanged.

## Formal runtime cutover completed (2026-10-12)

The user subsequently requested that formal daily and weekly reports also use QQ-to-Gmail. This authorized the runtime cutover, superseding the earlier not-deployed status above.

- Added `Paulatim/Migrate-MailToQq.ps1`: require matching verified identities/source, an accepted verification-only SMTP ledger, an available vault credential, idle owned task, no pending legacy delivery and no second enabled mail task. Back up private configuration, executables and receipts; disable the existing task and hold its mail lease while switching. Do not copy the staged legacy receipt file over live receipts. Configuration/action rollback before activation never restores stale receipts.
- Eight isolated migration checks passed: read-only preflight, successful cutover, pending legacy job, identity mismatch, second sender, uncertain SMTP ledger, task-update failure and active mail lock. Credential and scheduler adapters were mocked; file preservation and rollback used real synthetic files. Existing SMTP fault checks passed 18/18 and branding checks passed 5/5 again.
- Live preflight passed. One cutover invocation correctly refused while the minute-level task was running; the task naturally returned idle before the successful retry. No active sender was killed.
- The existing `WorkLogMail-Dispatch` task now runs `D:\WorkLogSync\PaulatimDispatch.exe`, with regular mail enabled and `transport=qq_smtp`. The runtime also contains the compatible `Paulatim.exe` and verified QQ configuration/SMTP ledger. The original sync task remains in service; no new mail task was created.
- Post-cutover XML comparison confirmed unchanged triggers, principal and settings. The live legacy receipt file is byte-identical to its pre-cutover backup. The new controller completed with `idle`; the already-receipted 2026-10-11 daily report was not resent.
- Daily delivery remains 08:00 Beijing for the previous day. With the unchanged first weekly end of 2026-10-17, the first eligible weekly report is due 2026-10-18 at 08:00, covering October 11–17. Login catch-up and retry triggers remain intact.

This cutover sent no new verification or formal message because no period was due. It confirms the selected production transport and successful idle execution, not receipt of a future formal report. The earlier verification's Gmail readback remains the actual delivery evidence. No Category parser or production iCloud input change was made. Private backup and verification files stay outside Git.

## First formal daily recovery and readback (2026-10-13)

The user reported no received report. The formal daily period `2026-10-12` had no receipt; synchronization and task execution were healthy, but its SMTP ledger was `uncertain` after `SmtpException` with general status `-1`. Subsequent ticks did not resend that uncertain payload. The original exception chain was not persisted, so the exact cause/SMTP phase of that first failure cannot be established from the ledger.

Gmail whole-mailbox searches found no matching formal daily, including a final check before recovery. A strict-TLS, AUTH-only diagnostic returned 235 without submitting mail. The user explicitly authorized exactly one controlled retry with the original frozen body/attachment, after being informed of the residual delayed-duplicate risk.

Recovery held the existing mail lease, preserved private ledger/configuration backups, restored only that period to prepared, and used a single-use invocation marker. Two preparation-only script failures occurred before the sending API was called; their reserved-variable/type-conversion issues were corrected without repeating an external send. The one external send was then accepted, recorded under the original period identity, and all older receipts remained unchanged. No scheduled task, credential, source CSV or runtime binary was modified.

Gmail INBOX contained one matching formal report received at **2026-10-13 10:24 Beijing**. Seven raw-MIME checks passed: sender, recipient, Paulatim display name, formal subject, exact frozen HTML, byte-identical 304-byte original CSV, and observed server Message-ID. Private evidence retains both client/server IDs and the Gmail ID. The controller returned idle with zero attempts and a confirmed daily receipt, suppressing another send for this period.

This verifies the recovered formal delivery, not future availability. Remaining diagnostic work includes persisting safe exception/transport-stage details and surfacing attention-required status; SMTP acceptance alone still does not prove inbox delivery. No automatic uncertain-outcome retry was added.
