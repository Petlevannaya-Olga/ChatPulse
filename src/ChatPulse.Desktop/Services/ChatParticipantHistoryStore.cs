using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// A verified occurrence in a complete Telegram participant list.
/// These are observations of profiles, not a claim about a person's real identity.
/// The parser supplies separate weekend/penalty flags, even when both are set.
/// </summary>
public sealed record ParticipantHistoryEntry(
    string SubscriptionProfileUrl,
    string ActivityProfileUrl,
    string Category,
    bool IsWeekend = false,
    bool IsPenalty = false,
    int? Position = null,
    string? DisplayName = null,
    string SourceSection = "ВСЕ МЫ",
    string? SourceProfileUrl = null,
    DateOnly? WeekendFromDate = null,
    DateOnly? WeekendToDate = null,
    string? WeekendSourceText = null);

/// <summary>
/// The caller discovers *candidate* Telegram messages and validates them under
/// rules specific to this chat. Null Entries means parsing failed; such a message
/// is reported but never used to infer departures. Raw text is used for edit hashes.
/// </summary>
public sealed record ParticipantHistoryCandidate(
    long MessageId,
    DateTime? SentAtUtc,
    string SourceText,
    IReadOnlyList<ParticipantHistoryEntry>? Entries,
    string? ParseErrorDescription = null);

public sealed record ParticipantHistoryRule(int Version, string ParserKind, string ConfigurationJson = "{}");

public sealed record ParticipantHistoryRebuildResult(
    int FoundCount, int AcceptedCount, int RejectedCount,
    string Outcome, long? OldestMessageId, long? NewestMessageId);

public sealed record ParticipantHistorySnapshotInfo(long MessageId, string ParseStatus, string? Error);

public sealed record ParticipantHistoryStateInfo(
    string InstagramUsername,
    long MessageId,
    bool IsPresent,
    string? Category,
    bool IsWeekend,
    bool IsPenalty,
    string? ActivityProfileUrl,
    DateOnly? WeekendFromDate,
    DateOnly? WeekendToDate);

/// <summary>
/// Backend for rebuilding a chat's permanent history from verified Telegram list
/// snapshots. This does not modify ChatParticipants, its latest-list version or
/// the existing subscription check results. Telegram traversal and the new UI are
/// deliberately separate steps.
/// </summary>
public sealed class ChatParticipantHistoryStore(string databasePath)
{
    private sealed record VerifiedEntry(
        ParticipantHistoryEntry Raw,
        string Username,
        string SubscriptionUrl,
        string ActivityUrl);

    private sealed record SnapshotPreparation(
        ParticipantHistoryCandidate Source,
        IReadOnlyList<VerifiedEntry> RawEntries,
        IReadOnlyList<VerifiedEntry> UniqueEntries,
        string? Error)
    {
        public bool IsParsed => Error is null;
    }

    private sealed record StateValue(
        bool IsPresent,
        string? Category,
        bool IsWeekend,
        bool IsPenalty,
        string? SubscriptionUrl,
        string? ActivityUrl,
        string? WeekendFrom,
        string? WeekendTo,
        string? WeekendSourceText);

    private sealed class Identity(long id, StateValue lastState)
    {
        public long Id { get; } = id;
        public StateValue LastState { get; set; } = lastState;
    }

    /// <summary>
    /// Atomic, chat-isolated replacement of the reconstructed history. All candidate
    /// messages, including rejected ones, are recorded. A rejected list does not
    /// mark anyone as absent. Replaying an identical history yields the same states.
    /// </summary>
    public async Task<ParticipantHistoryRebuildResult> RebuildAsync(
        ConfiguredChat chat,
        IEnumerable<ParticipantHistoryCandidate> candidates,
        ParticipantHistoryRule rule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(rule);
        if (chat.TelegramUserId <= 0 || chat.PeerId == 0 || string.IsNullOrWhiteSpace(chat.PeerType))
            throw new ArgumentException("Не указан полный идентификатор Telegram-чата.", nameof(chat));
        if (rule.Version < 1 || string.IsNullOrWhiteSpace(rule.ParserKind))
            throw new ArgumentException("Некорректная версия правил чата.", nameof(rule));
        if (string.IsNullOrWhiteSpace(rule.ConfigurationJson))
            throw new ArgumentException("Не задана конфигурация правил.", nameof(rule));

        var sources = candidates.OrderBy(item => item.MessageId).ToArray();
        if (sources.Length == 0)
            throw new ArgumentException("Для полного восстановления необходима история сообщений.", nameof(candidates));
        if (sources.Any(item => item.MessageId <= 0 || item.SourceText is null) ||
            sources.Select(item => item.MessageId).Distinct().Count() != sources.Length)
            throw new ArgumentException("Неверные или повторяющиеся идентификаторы сообщений.", nameof(candidates));

        // Validate before altering any persisted state. Bad individual messages
        // become REJECTED, not a reason to discard otherwise usable observations.
        var prepared = sources.Select(Prepare).ToArray();
        var acceptedCount = prepared.Count(item => item.IsParsed);
        var rejectedCount = prepared.Length - acceptedCount;
        var outcome = acceptedCount == 0 ? "FAILED" :
            rejectedCount == 0 ? "COMPLETED" : "PARTIAL";

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var ruleId = await EnsureRuleAsync(connection, transaction, chat, rule, cancellationToken);
            // Do not destroy previously reconstructed history when all candidates
            // are malformed. The run report still records every failure.
            if (acceptedCount == 0)
            {
                await InsertRunAsync(connection, transaction, chat, prepared, outcome,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new ParticipantHistoryRebuildResult(prepared.Length, 0,
                    rejectedCount, outcome, sources[0].MessageId, sources[^1].MessageId);
            }
            await DeletePriorHistoryAsync(connection, transaction, chat, cancellationToken);
            var identities = new Dictionary<string, Identity>(StringComparer.OrdinalIgnoreCase);

            foreach (var snapshot in prepared)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshotId = await InsertSnapshotAsync(connection, transaction, chat,
                    ruleId, snapshot, cancellationToken);
                if (!snapshot.IsParsed) continue;

                for (var i = 0; i < snapshot.RawEntries.Count; i++)
                    await InsertEntryAsync(connection, transaction, snapshotId, i,
                        snapshot.RawEntries[i], cancellationToken);

                var currentlyPresent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in snapshot.UniqueEntries)
                {
                    currentlyPresent.Add(entry.Username);
                    var nextState = StateFromEntry(entry);
                    if (!identities.TryGetValue(entry.Username, out var identity))
                    {
                        var id = await InsertIdentityAsync(connection, transaction, chat,
                            entry.Username, snapshotId, cancellationToken);
                        identity = new Identity(id, nextState);
                        identities.Add(entry.Username, identity);
                        await InsertStateAsync(connection, transaction, id, snapshotId,
                            nextState, cancellationToken);
                        continue;
                    }

                    await UpdateLastSeenAsync(connection, transaction, identity.Id,
                        snapshotId, cancellationToken);
                    if (identity.LastState == nextState) continue;
                    identity.LastState = nextState;
                    await InsertStateAsync(connection, transaction, identity.Id,
                        snapshotId, nextState, cancellationToken);
                }

                foreach (var (username, identity) in identities)
                {
                    if (currentlyPresent.Contains(username) || !identity.LastState.IsPresent) continue;
                    // Absence is observed at this complete list's publication,
                    // not proof of the exact moment that someone left.
                    var absent = new StateValue(false, null, false, false,
                        null, null, null, null, null);
                    identity.LastState = absent;
                    await InsertStateAsync(connection, transaction, identity.Id,
                        snapshotId, absent, cancellationToken);
                }
            }

            await InsertRunAsync(connection, transaction, chat, prepared,
                outcome, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        return new ParticipantHistoryRebuildResult(prepared.Length, acceptedCount,
            rejectedCount, outcome, sources[0].MessageId, sources[^1].MessageId);
    }

    private static async Task InsertRunAsync(SqliteConnection connection, SqliteTransaction transaction,
        ConfiguredChat chat, IReadOnlyList<SnapshotPreparation> prepared, string outcome,
        CancellationToken cancellationToken)
    {
        using var run = NewCommand(connection, transaction, """
            INSERT INTO ChatParticipantHistoryRuns
                (TelegramUserId, PeerType, PeerId, Mode, Outcome, StartedAtUnixSeconds,
                 CompletedAtUnixSeconds, OldestFoundMessageId, NewestFoundMessageId,
                 CandidateCount, AcceptedCount, RejectedCount, ErrorDescription)
            VALUES ($owner, $type, $peer, 'FULL', $outcome, $now, $now,
                    $oldest, $newest, $total, $accepted, $rejected, $error)
            """);
        AddChatParameters(run, chat);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rejected = prepared.Where(item => !item.IsParsed).ToArray();
        run.Parameters.AddWithValue("$now", now);
        run.Parameters.AddWithValue("$outcome", outcome);
        run.Parameters.AddWithValue("$oldest", prepared[0].Source.MessageId);
        run.Parameters.AddWithValue("$newest", prepared[^1].Source.MessageId);
        run.Parameters.AddWithValue("$total", prepared.Count);
        run.Parameters.AddWithValue("$accepted", prepared.Count - rejected.Length);
        run.Parameters.AddWithValue("$rejected", rejected.Length);
        run.Parameters.AddWithValue("$error", rejected.Length == 0 ? DBNull.Value :
            string.Join("; ", rejected.Select(item => $"№{item.Source.MessageId}: {item.Error}")));
        await run.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<(string Outcome, string? Errors)?> ReadLastRunAsync(
        ConfiguredChat chat, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(databasePath)) return null;
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        using var command = NewCommand(connection, null, """
            SELECT Outcome, ErrorDescription FROM ChatParticipantHistoryRuns
            WHERE TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer
            ORDER BY RunId DESC LIMIT 1
            """);
        AddChatParameters(command, chat);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    public async Task<IReadOnlyList<ParticipantHistoryStateInfo>> ReadChangesAsync(
        ConfiguredChat chat, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(databasePath)) return [];
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        var command = NewCommand(connection, null, """
            SELECT i.InstagramUsername, s.SourceMessageId,
                   h.IsPresent, h.Category, h.IsWeekend, h.IsPenalty,
                   h.ActivityProfileUrl, h.WeekendFromDate, h.WeekendToDate
            FROM ChatParticipantHistoryStates h
            JOIN ChatParticipantHistoryIdentities i ON i.HistoryParticipantId=h.HistoryParticipantId
            JOIN ChatParticipantListSnapshots s ON s.SnapshotId=h.SnapshotId
            WHERE i.TelegramUserId=$owner AND i.PeerType=$type AND i.PeerId=$peer
            ORDER BY s.SourceMessageId, i.InstagramUsername
            """);
        AddChatParameters(command, chat);
        var result = new List<ParticipantHistoryStateInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new ParticipantHistoryStateInfo(reader.GetString(0), reader.GetInt64(1),
                reader.GetInt64(2) != 0, reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4) != 0, reader.GetInt64(5) != 0,
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : DateOnly.ParseExact(reader.GetString(7), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.IsDBNull(8) ? null : DateOnly.ParseExact(reader.GetString(8), "yyyy-MM-dd", CultureInfo.InvariantCulture)));
        return result;
    }

    public async Task<IReadOnlyList<ParticipantHistoryStateInfo>> ReadCurrentAsync(
        ConfiguredChat chat, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(databasePath)) return [];
        var changes = await ReadChangesAsync(chat, cancellationToken);
        return changes.GroupBy(item => item.InstagramUsername, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last()).Where(item => item.IsPresent)
            .OrderBy(item => item.InstagramUsername, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<IReadOnlyList<ParticipantHistorySnapshotInfo>> ReadSnapshotsAsync(
        ConfiguredChat chat, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(databasePath)) return [];
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        var command = NewCommand(connection, null, """
            SELECT SourceMessageId, ParseStatus, ParseErrorDescription
            FROM ChatParticipantListSnapshots
            WHERE TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer
            ORDER BY SourceMessageId
            """);
        AddChatParameters(command, chat);
        var result = new List<ParticipantHistorySnapshotInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new ParticipantHistorySnapshotInfo(reader.GetInt64(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        return result;
    }

    private static SnapshotPreparation Prepare(ParticipantHistoryCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.ParseErrorDescription))
            return new SnapshotPreparation(candidate, [], [], candidate.ParseErrorDescription);
        if (candidate.Entries is not { Count: > 0 })
            return new SnapshotPreparation(candidate, [], [], "Не удалось разобрать полный список участниц.");

        var raw = new List<VerifiedEntry>();
        foreach (var entry in candidate.Entries)
        {
            if (!TryNormalizeProfile(entry.SubscriptionProfileUrl, out var username, out var subscription) ||
                !TryNormalizeProfile(entry.ActivityProfileUrl, out _, out var activity) ||
                string.IsNullOrWhiteSpace(entry.Category) || string.IsNullOrWhiteSpace(entry.SourceSection) ||
                entry.WeekendFromDate is { } from && entry.WeekendToDate is { } to && from > to)
                return new SnapshotPreparation(candidate, [], [],
                    $"Некорректная запись №{entry.Position?.ToString(CultureInfo.InvariantCulture) ?? "?"}: " +
                    "профиль, категория, раздел или период выходных.");
            raw.Add(new VerifiedEntry(entry, username, subscription, activity));
        }

        var unique = new List<VerifiedEntry>();
        foreach (var group in raw.GroupBy(item => item.Username, StringComparer.OrdinalIgnoreCase))
        {
            // Repeated rows in weekend/penalty sections add flags only;
            // category and activity profile come from the principal section.
            var primaryRows = group.Where(item => !IsSupplementalSection(item.Raw.SourceSection)).ToArray();
            var primary = primaryRows.FirstOrDefault() ?? group.First();
            if (primaryRows.Any(item => item.ActivityUrl != primary.ActivityUrl ||
                !string.Equals(item.Raw.Category, primary.Raw.Category, StringComparison.OrdinalIgnoreCase)))
                return new SnapshotPreparation(candidate, [], [],
                    $"Противоречащие записи для @{group.Key} в одном списке.");

            var periods = group.Where(item => item.Raw.WeekendFromDate is not null ||
                item.Raw.WeekendToDate is not null)
                .Select(item => (item.Raw.WeekendFromDate, item.Raw.WeekendToDate))
                .Distinct().ToArray();
            if (periods.Length > 1)
                return new SnapshotPreparation(candidate, [], [],
                    $"Противоречащие даты выходных для @{group.Key}.");

            var merged = primary.Raw with
            {
                IsWeekend = group.Any(item => item.Raw.IsWeekend ||
                    item.Raw.SourceSection.Contains("ВЫХОДНЫ", StringComparison.OrdinalIgnoreCase)),
                IsPenalty = group.Any(item => item.Raw.IsPenalty ||
                    item.Raw.SourceSection.Contains("ШТРАФНЫ", StringComparison.OrdinalIgnoreCase)),
                WeekendFromDate = group.Select(item => item.Raw.WeekendFromDate).FirstOrDefault(item => item.HasValue),
                WeekendToDate = group.Select(item => item.Raw.WeekendToDate).FirstOrDefault(item => item.HasValue),
                WeekendSourceText = group.Select(item => item.Raw.WeekendSourceText)
                    .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))
            };
            unique.Add(primary with { Raw = merged });
        }
        return new SnapshotPreparation(candidate, raw, unique, null);
    }

    private static bool IsSupplementalSection(string section)
        => section.Contains("ШТРАФНЫ", StringComparison.OrdinalIgnoreCase) ||
           section.Contains("ВЫХОДНЫ", StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalizeProfile(string? input, out string username, out string url)
    {
        username = string.Empty;
        url = string.Empty;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host is not "instagram.com" and not "www.instagram.com" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 1) return false;
        try { username = InstagramUserName.Normalize(Uri.UnescapeDataString(segments[0])); }
        catch (ArgumentException) { return false; }
        url = $"https://www.instagram.com/{username}";
        return true;
    }

    private static StateValue StateFromEntry(VerifiedEntry entry)
    {
        var item = entry.Raw;
        return new StateValue(true, item.Category, item.IsWeekend, item.IsPenalty,
            entry.SubscriptionUrl, entry.ActivityUrl,
            item.WeekendFromDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            item.WeekendToDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            item.WeekendSourceText);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys=ON";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var assembly = typeof(ChatParticipantHistoryStore).Assembly;
        const string RESOURCE_NAME = "ChatPulse.Desktop.Services.ChatParticipantHistorySchema.sql";
        await using var stream = assembly.GetManifestResourceStream(RESOURCE_NAME)
            ?? throw new InvalidOperationException($"Отсутствует ресурс схемы: {RESOURCE_NAME}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var sql = await reader.ReadToEndAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqliteCommand NewCommand(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void AddChatParameters(SqliteCommand command, ConfiguredChat chat)
    {
        command.Parameters.AddWithValue("$owner", chat.TelegramUserId);
        command.Parameters.AddWithValue("$type", chat.PeerType);
        command.Parameters.AddWithValue("$peer", chat.PeerId);
    }

    private static async Task<long> InsertAndReturnIdAsync(SqliteCommand command,
        CancellationToken cancellationToken)
    {
        await command.ExecuteNonQueryAsync(cancellationToken);
        using var last = NewCommand(command.Connection!, command.Transaction as SqliteTransaction,
            "SELECT last_insert_rowid()");
        return Convert.ToInt64(await last.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<long> EnsureRuleAsync(SqliteConnection connection, SqliteTransaction transaction,
        ConfiguredChat chat, ParticipantHistoryRule rule, CancellationToken cancellationToken)
    {
        using var command = NewCommand(connection, transaction, """
            INSERT OR IGNORE INTO ChatParticipantRuleVersions
                (TelegramUserId, PeerType, PeerId, Version, ParserKind, ConfigurationJson, CreatedAtUnixSeconds)
            VALUES ($owner, $type, $peer, $version, $parser, $json, $created)
            """);
        AddChatParameters(command, chat);
        command.Parameters.AddWithValue("$version", rule.Version);
        command.Parameters.AddWithValue("$parser", rule.ParserKind);
        command.Parameters.AddWithValue("$json", rule.ConfigurationJson);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
        using var select = NewCommand(connection, transaction, """
            SELECT RuleVersionId, ParserKind, ConfigurationJson
            FROM ChatParticipantRuleVersions
            WHERE TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer AND Version=$version
            """);
        AddChatParameters(select, chat);
        select.Parameters.AddWithValue("$version", rule.Version);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Не удалось сохранить версию правил.");
        if (reader.GetString(1) != rule.ParserKind || reader.GetString(2) != rule.ConfigurationJson)
            throw new InvalidOperationException("Для изменённых правил требуется новая версия конфигурации чата.");
        return reader.GetInt64(0);
    }

    private static async Task DeletePriorHistoryAsync(SqliteConnection connection, SqliteTransaction transaction,
        ConfiguredChat chat, CancellationToken cancellationToken)
    {
        string[] sqls =
        [
            """DELETE FROM ChatParticipantHistoryStates WHERE HistoryParticipantId IN (SELECT HistoryParticipantId FROM ChatParticipantHistoryIdentities WHERE TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer)""",
            """DELETE FROM ChatParticipantHistoryIdentities WHERE TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer""",
            """DELETE FROM ChatParticipantListEntries WHERE SnapshotId IN (SELECT SnapshotId FROM ChatParticipantListSnapshots WHERE TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer)""",
            """DELETE FROM ChatParticipantListSnapshots WHERE TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer"""
        ];
        foreach (var sql in sqls)
        {
            using var command = NewCommand(connection, transaction, sql);
            AddChatParameters(command, chat);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<long> InsertSnapshotAsync(SqliteConnection connection, SqliteTransaction transaction,
        ConfiguredChat chat, long ruleId, SnapshotPreparation item, CancellationToken cancellationToken)
    {
        using var command = NewCommand(connection, transaction, """
            INSERT INTO ChatParticipantListSnapshots
                (TelegramUserId, PeerType, PeerId, SourceMessageId, SentAtUnixSeconds,
                 CheckedAtUnixSeconds, SourceTextSha256, RuleVersionId, ParseStatus,
                 ParseErrorCode, ParseErrorDescription)
            VALUES ($owner, $type, $peer, $message, $sent, $checked, $hash,
                    $rule, $status, $code, $description)
            """);
        AddChatParameters(command, chat);
        command.Parameters.AddWithValue("$message", item.Source.MessageId);
        command.Parameters.AddWithValue("$sent", item.Source.SentAtUtc is { } sentAt
            ? new DateTimeOffset(sentAt.ToUniversalTime()).ToUnixTimeSeconds() : DBNull.Value);
        command.Parameters.AddWithValue("$checked", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$hash", Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(item.Source.SourceText))));
        command.Parameters.AddWithValue("$rule", ruleId);
        command.Parameters.AddWithValue("$status", item.IsParsed ? "PARSED" : "REJECTED");
        command.Parameters.AddWithValue("$code", item.IsParsed ? DBNull.Value : "LIST_PARSE_FAILED");
        command.Parameters.AddWithValue("$description", (object?)item.Error ?? DBNull.Value);
        return await InsertAndReturnIdAsync(command, cancellationToken);
    }

    private static async Task InsertEntryAsync(SqliteConnection connection, SqliteTransaction transaction,
        long snapshotId, int ordinal, VerifiedEntry entry, CancellationToken cancellationToken)
    {
        using var command = NewCommand(connection, transaction, """
            INSERT INTO ChatParticipantListEntries
                (SnapshotId, SourceOrdinal, SourcePosition, SourceSection,
                 InstagramUsername, SourceProfileUrl, SubscriptionProfileUrl, ActivityProfileUrl,
                 DisplayName, Category, IsWeekend, IsPenalty,
                 WeekendFromDate, WeekendToDate, WeekendSourceText)
            VALUES ($snapshot, $ordinal, $position, $section, $username, $source,
                    $subscription, $activity, $name, $category, $weekend, $penalty,
                    $from, $to, $note)
            """);
        var raw = entry.Raw;
        command.Parameters.AddWithValue("$snapshot", snapshotId);
        command.Parameters.AddWithValue("$ordinal", ordinal);
        command.Parameters.AddWithValue("$position", (object?)raw.Position ?? DBNull.Value);
        command.Parameters.AddWithValue("$section", raw.SourceSection);
        command.Parameters.AddWithValue("$username", entry.Username);
        command.Parameters.AddWithValue("$source", raw.SourceProfileUrl ?? raw.SubscriptionProfileUrl);
        command.Parameters.AddWithValue("$subscription", entry.SubscriptionUrl);
        command.Parameters.AddWithValue("$activity", entry.ActivityUrl);
        command.Parameters.AddWithValue("$name", (object?)raw.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("$category", raw.Category);
        command.Parameters.AddWithValue("$weekend", raw.IsWeekend ? 1 : 0);
        command.Parameters.AddWithValue("$penalty", raw.IsPenalty ? 1 : 0);
        command.Parameters.AddWithValue("$from", (object?)raw.WeekendFromDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", (object?)raw.WeekendToDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$note", (object?)raw.WeekendSourceText ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> InsertIdentityAsync(SqliteConnection connection, SqliteTransaction transaction,
        ConfiguredChat chat, string username, long snapshotId, CancellationToken cancellationToken)
    {
        using var command = NewCommand(connection, transaction, """
            INSERT INTO ChatParticipantHistoryIdentities
                (TelegramUserId, PeerType, PeerId, InstagramUsername, FirstSeenSnapshotId, LastSeenSnapshotId)
            VALUES ($owner, $type, $peer, $username, $first, $last)
            """);
        AddChatParameters(command, chat);
        command.Parameters.AddWithValue("$username", username);
        command.Parameters.AddWithValue("$first", snapshotId);
        command.Parameters.AddWithValue("$last", snapshotId);
        return await InsertAndReturnIdAsync(command, cancellationToken);
    }

    private static async Task UpdateLastSeenAsync(SqliteConnection connection, SqliteTransaction transaction,
        long identityId, long snapshotId, CancellationToken cancellationToken)
    {
        using var command = NewCommand(connection, transaction, """
            UPDATE ChatParticipantHistoryIdentities
            SET LastSeenSnapshotId=$snapshot WHERE HistoryParticipantId=$identity
            """);
        command.Parameters.AddWithValue("$snapshot", snapshotId);
        command.Parameters.AddWithValue("$identity", identityId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertStateAsync(SqliteConnection connection, SqliteTransaction transaction,
        long identityId, long snapshotId, StateValue state, CancellationToken cancellationToken)
    {
        using var command = NewCommand(connection, transaction, """
            INSERT INTO ChatParticipantHistoryStates
                (HistoryParticipantId, SnapshotId, IsPresent, Category, IsWeekend,
                 IsPenalty, SubscriptionProfileUrl, ActivityProfileUrl,
                 WeekendFromDate, WeekendToDate, WeekendSourceText)
            VALUES ($identity, $snapshot, $present, $category, $weekend, $penalty,
                    $subscription, $activity, $from, $to, $note)
            """);
        command.Parameters.AddWithValue("$identity", identityId);
        command.Parameters.AddWithValue("$snapshot", snapshotId);
        command.Parameters.AddWithValue("$present", state.IsPresent ? 1 : 0);
        command.Parameters.AddWithValue("$category", (object?)state.Category ?? DBNull.Value);
        command.Parameters.AddWithValue("$weekend", state.IsWeekend ? 1 : 0);
        command.Parameters.AddWithValue("$penalty", state.IsPenalty ? 1 : 0);
        command.Parameters.AddWithValue("$subscription", (object?)state.SubscriptionUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$activity", (object?)state.ActivityUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$from", (object?)state.WeekendFrom ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", (object?)state.WeekendTo ?? DBNull.Value);
        command.Parameters.AddWithValue("$note", (object?)state.WeekendSourceText ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
