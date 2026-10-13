# Minit project memory

Start with [the project reference](docs/PROJECT_REFERENCE.md) for the source-based analysis of both MinitApi and MinitAppAndroid, reviewed 2026-10-13.

Minit is a display-name/code-based Android audio/video calling and encrypted text messaging app. Backend: .NET 10 Minimal API, EF Core/PostgreSQL, Firebase Admin, in-memory WebSocket signaling, monthly creator-only quotas, cleanup workers. Android: Kotlin/Compose/Hilt, Retrofit/OkHttp, DataStore, WebRTC, FCM and AndroidKeyStore hybrid encryption. Default quota is 6,000 seconds per UTC month.

Critical snapshot facts: message endpoints are mapped twice, causing duplicate endpoint-name routing failure; all 13 API integration tests failed at review. Identity is caller-supplied without user authentication. Invites are stored raw despite hashing claims in CLAUDE.md. Recent calls do not use the call_history retention table. Billing starts at call creation, uses end month, and has no live quota cutoff. DATABASE_URL is required outside Testing. Current API hosts signaling at /ws; separate Node signaling setup prose is stale.

See the full reference for evidence, exact contracts, decisions, limitations and follow-up questions. These are review findings, not approved changes. No application fixes were made during the review. Never copy credentials into this memory.
