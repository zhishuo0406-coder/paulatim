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
