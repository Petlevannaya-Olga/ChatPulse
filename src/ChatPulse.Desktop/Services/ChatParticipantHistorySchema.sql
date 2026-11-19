-- ChatPulse: permanent Telegram participant register history (design v1)
-- Standalone ADDITIVE schema. It does not modify existing ChatParticipants,
-- ChatParticipantRegistryVersions or the last-refresh reporting tables.
-- All periods are observations of published Telegram lists, not proven real-world dates.
-- For each SQLite connection that writes these tables, ensure PRAGMA foreign_keys=ON.
PRAGMA foreign_keys=ON;
BEGIN IMMEDIATE;

-- Chat-specific parser rules are versioned so old list snapshots retain the rule
-- that produced them. Application code decides which version is active.
CREATE TABLE IF NOT EXISTS ChatParticipantRuleVersions (
    RuleVersionId          INTEGER PRIMARY KEY,
    TelegramUserId         INTEGER NOT NULL,
    PeerType               TEXT NOT NULL,
    PeerId                 INTEGER NOT NULL,
    Version                INTEGER NOT NULL CHECK (Version >= 1),
    ParserKind             TEXT NOT NULL CHECK (LENGTH(TRIM(ParserKind)) > 0),
    ConfigurationJson      TEXT NOT NULL CHECK (json_valid(ConfigurationJson)),
    CreatedAtUnixSeconds   INTEGER NOT NULL,
    UNIQUE (TelegramUserId, PeerType, PeerId, Version)
);

-- One run per explicit action: only the newest list (LATEST), or the full
-- available history (FULL). Runs are persisted for progress and error reports.
CREATE TABLE IF NOT EXISTS ChatParticipantHistoryRuns (
    RunId                   INTEGER PRIMARY KEY,
    TelegramUserId          INTEGER NOT NULL,
    PeerType                TEXT NOT NULL,
    PeerId                  INTEGER NOT NULL,
    Mode                    TEXT NOT NULL CHECK (Mode IN ('LATEST', 'FULL')),
    Outcome                 TEXT NOT NULL CHECK (Outcome IN ('RUNNING', 'COMPLETED', 'PARTIAL', 'FAILED', 'STOPPED')),
    StartedAtUnixSeconds    INTEGER NOT NULL,
    CompletedAtUnixSeconds  INTEGER,
    OldestFoundMessageId    INTEGER,
    NewestFoundMessageId    INTEGER,
    CandidateCount          INTEGER NOT NULL DEFAULT 0 CHECK (CandidateCount >= 0),
    AcceptedCount           INTEGER NOT NULL DEFAULT 0 CHECK (AcceptedCount >= 0),
    RejectedCount           INTEGER NOT NULL DEFAULT 0 CHECK (RejectedCount >= 0),
    ErrorDescription        TEXT
);
CREATE INDEX IF NOT EXISTS IX_ChatParticipantHistoryRuns_Chat
    ON ChatParticipantHistoryRuns (TelegramUserId, PeerType, PeerId, StartedAtUnixSeconds DESC);

-- One snapshot per Telegram message. ContentHash detects Telegram edits to an
-- already-seen message id; changing an old snapshot requires a downstream rebuild.
-- REJECTED records are never used to mark participants as departed.
CREATE TABLE IF NOT EXISTS ChatParticipantListSnapshots (
    SnapshotId             INTEGER PRIMARY KEY,
    TelegramUserId         INTEGER NOT NULL,
    PeerType               TEXT NOT NULL,
    PeerId                 INTEGER NOT NULL,
    SourceMessageId        INTEGER NOT NULL CHECK (SourceMessageId > 0),
    SentAtUnixSeconds      INTEGER,
    CheckedAtUnixSeconds   INTEGER NOT NULL,
    SourceTextSha256       TEXT NOT NULL CHECK (LENGTH(SourceTextSha256) = 64),
    RuleVersionId          INTEGER,
    ParseStatus            TEXT NOT NULL CHECK (ParseStatus IN ('PARSED', 'REJECTED')),
    ParseErrorCode         TEXT,
    ParseErrorDescription  TEXT,
    FOREIGN KEY (RuleVersionId) REFERENCES ChatParticipantRuleVersions(RuleVersionId),
    UNIQUE (TelegramUserId, PeerType, PeerId, SourceMessageId)
);
CREATE INDEX IF NOT EXISTS IX_ChatParticipantListSnapshots_ChatOrder
    ON ChatParticipantListSnapshots (TelegramUserId, PeerType, PeerId, SourceMessageId);

-- Raw, verified entries from a successfully parsed message. Ordinal is a source
-- occurrence, NOT a participant identity: exact duplicates can be retained here.
CREATE TABLE IF NOT EXISTS ChatParticipantListEntries (
    EntryId                 INTEGER PRIMARY KEY,
    SnapshotId              INTEGER NOT NULL REFERENCES ChatParticipantListSnapshots(SnapshotId) ON DELETE CASCADE,
    SourceOrdinal           INTEGER NOT NULL CHECK (SourceOrdinal >= 0),
    SourcePosition          INTEGER,
    SourceSection           TEXT NOT NULL,
    InstagramUsername       TEXT NOT NULL CHECK (LENGTH(TRIM(InstagramUsername)) > 0),
    SourceProfileUrl        TEXT NOT NULL,
    SubscriptionProfileUrl  TEXT NOT NULL,
    ActivityProfileUrl      TEXT NOT NULL,
    DisplayName             TEXT,
    Category                TEXT NOT NULL,
    IsWeekend               INTEGER NOT NULL DEFAULT 0 CHECK (IsWeekend IN (0, 1)),
    IsPenalty               INTEGER NOT NULL DEFAULT 0 CHECK (IsPenalty IN (0, 1)),
    WeekendFromDate         TEXT,
    WeekendToDate           TEXT,
    WeekendSourceText       TEXT,
    UNIQUE (SnapshotId, SourceOrdinal)
);
CREATE INDEX IF NOT EXISTS IX_ChatParticipantListEntries_SnapshotUsername
    ON ChatParticipantListEntries (SnapshotId, InstagramUsername);

-- The identity is always local to one Telegram chat. Username must be lowercased
-- and canonicalized by the application before insertion. A renamed profile is
-- deliberately treated as a NEW identity, per the product rule.
CREATE TABLE IF NOT EXISTS ChatParticipantHistoryIdentities (
    HistoryParticipantId  INTEGER PRIMARY KEY,
    TelegramUserId         INTEGER NOT NULL,
    PeerType               TEXT NOT NULL,
    PeerId                 INTEGER NOT NULL,
    InstagramUsername      TEXT NOT NULL COLLATE NOCASE CHECK (LENGTH(TRIM(InstagramUsername)) > 0),
    FirstSeenSnapshotId    INTEGER NOT NULL REFERENCES ChatParticipantListSnapshots(SnapshotId),
    LastSeenSnapshotId     INTEGER NOT NULL REFERENCES ChatParticipantListSnapshots(SnapshotId),
    UNIQUE (TelegramUserId, PeerType, PeerId, InstagramUsername)
);
CREATE INDEX IF NOT EXISTS IX_ChatParticipantHistoryIdentities_Chat
    ON ChatParticipantHistoryIdentities (TelegramUserId, PeerType, PeerId);

-- State history records every TRANSITION: join, leave, return, category change,
-- weekend/penalty start or end, activity profile change. IsPresent=0 means absent
-- from a successfully parsed complete list; it is NOT an exact departure time.
-- Weekend and penalty flags are independent and may both be set.
CREATE TABLE IF NOT EXISTS ChatParticipantHistoryStates (
    StateId                INTEGER PRIMARY KEY,
    HistoryParticipantId   INTEGER NOT NULL REFERENCES ChatParticipantHistoryIdentities(HistoryParticipantId),
    SnapshotId             INTEGER NOT NULL REFERENCES ChatParticipantListSnapshots(SnapshotId),
    IsPresent              INTEGER NOT NULL CHECK (IsPresent IN (0, 1)),
    Category               TEXT,
    IsWeekend              INTEGER NOT NULL DEFAULT 0 CHECK (IsWeekend IN (0, 1)),
    IsPenalty              INTEGER NOT NULL DEFAULT 0 CHECK (IsPenalty IN (0, 1)),
    SubscriptionProfileUrl TEXT,
    ActivityProfileUrl     TEXT,
    WeekendFromDate        TEXT,
    WeekendToDate          TEXT,
    WeekendSourceText      TEXT,
    UNIQUE (HistoryParticipantId, SnapshotId),
    CHECK (IsPresent = 1 OR (IsWeekend = 0 AND IsPenalty = 0))
);
CREATE INDEX IF NOT EXISTS IX_ChatParticipantHistoryStates_ParticipantSnapshot
    ON ChatParticipantHistoryStates (HistoryParticipantId, SnapshotId);

COMMIT;
