# PAU-002: Failed daily delivery stopped without an active notification

## Observed incident (2026-10-15, Beijing time)

The `2026-10-14` daily report entered `uncertain` at approximately 10:13 after a legacy `SmtpException` with status `-1`. Minute-level ticks subsequently consumed the controller's remaining attempts without another SMTP submission, then displayed a generic attempt-limit message. Synchronization continued successfully. Gmail whole-mailbox searches found no corresponding report. The old ledger did not retain the inner exception or submission stage, so the original network/SMTP cause cannot be reconstructed.

The user authorized one controlled resend and a lasting fix. Under the mail lease, the original ledgers were backed up and only this period was restored to `prepared`. The original frozen HTML and 477-byte CSV were submitted once using the existing QQ identity. SMTP accepted the report; Gmail INBOX received it at **14:07:59**. Seven independent raw-MIME checks passed: sender/display name, recipient, subject, exact HTML, byte-identical attachment, observed server Message-ID and retained original client ID. The daily receipt was recorded and the controller returned `idle`. No source data or historical receipt was removed.

## Changed behavior

- The QQ transport now observes connection, STARTTLS, TLS certificate validation, authentication, envelope, body upload and final acceptance separately. It uses TLS 1.2, the existing Windows credential reference and a bounded connection/read/write timeout. Credentials and server reply text are never written to diagnostics.
- Failure before the end-of-DATA marker, or an explicit SMTP rejection, leaves a safely retryable `prepared` delivery. Existing bounded retries run after 5 and 15 minutes. These diagnosed, unsubmitted payloads may be rebuilt from the freshly validated source so subsequent iCloud updates cannot block recovery behind an obsolete frozen payload. Legacy prepared payloads retain their original immutable behavior.
- Failure while sending the end-of-DATA marker or waiting for its acknowledgement remains `uncertain`. A crash after durable intent remains conservative as well. Neither situation triggers a blind resend. SMTP's final acceptance boundary is defined in [RFC 5321](https://www.rfc-editor.org/rfc/rfc5321.html#section-4.2.5).
- Safe diagnostics retain stage, exception type chain, socket error code and numeric SMTP reply code. Repeated ticks of an uncertain period preserve the cause and do not consume more attempts. Credential/configuration problems are reported as `attention_required`, rather than being mislabeled as source failures.
- A separate active chat heartbeat, **Paulatim 日报周报收件巡检**, checks at :15 each hour from 08:00 through 23:00 Beijing time. It checks local status freshness, due reports and Gmail receipt, caches verified message IDs, allows existing safe retries, and notifies on a new actionable failure or recovery. Uncertain outcomes require mailbox reconciliation. The old Gmail sender heartbeat remains paused; the new heartbeat does not add another scheduled sender.

## Validation and deployment

- 18 existing SMTP ledger checks passed.
- 14 new fault checks passed: success, AUTH refusal/disconnect, recipient rejection, interrupted body upload, ambiguous terminator write, lost/malformed final acknowledgement, explicit final rejection, malformed multiline replies, non-submitting probe, refreshed safe retry, diagnostic redaction, and repeated controller ticks without attempt consumption.
- Existing freshness checks passed: 17 core checks and 14 controller integration checks.
- Python's independent MIME parser confirmed the new transport's subject, HTML and exact CSV bytes. A live QQ probe passed STARTTLS, default hostname/certificate validation, authentication and recipient acceptance, then used RSET without submitting mail. The new transport's complete DATA path was tested offline; the incident's successful resend used the preceding transport. No extra test email was sent.
- Replaced only `D:\WorkLogSync\PaulatimDispatch.exe` after backing up the old binary and state. Byte comparisons confirmed configuration, both delivery ledgers and the saved task definition were unchanged by deployment. Post-deployment execution returned `idle` without another send.

The failure-handling gap is repaired, but the original transient failure's precise cause remains unknown. SMTP acceptance is still not proof of Gmail delivery. Local sending and the chat heartbeat require the relevant computer/application to be available; the next unattended daily delivery has not yet occurred at the time of this repair.
