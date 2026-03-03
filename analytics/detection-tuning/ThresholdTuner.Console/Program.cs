using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

var options = CliOptions.Parse(args);
if (options.ShowHelp)
{
    PrintUsage();
    return;
}

var databasePath = Path.GetFullPath(options.DatabasePath);
if (!File.Exists(databasePath))
{
    throw new FileNotFoundException("SQLite database file not found.", databasePath);
}

var rows = await LoadRowsAsync(databasePath, options, CancellationToken.None);
var report = BuildReport(databasePath, options, rows);

var outputPath = ResolveOutputPath(options.OutputPath);
var outputDir = Path.GetDirectoryName(outputPath);
if (!string.IsNullOrWhiteSpace(outputDir))
{
    Directory.CreateDirectory(outputDir);
}

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true
};
await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, jsonOptions));

Console.WriteLine($"Threshold report written: {outputPath}");
Console.WriteLine($"Rows analyzed: {report.TotalRowsAnalyzed}");
foreach (var channel in report.Channels)
{
    Console.WriteLine(
        $"- {channel.Channel}: rows={channel.RowCount}, p95={channel.Percentile95:F2}, p99={channel.Percentile99:F2}, review>={channel.RecommendedReviewThreshold:F2}, auto>={channel.RecommendedAutoThreshold:F2}");
}

static string ResolveOutputPath(string? outputPath)
{
    if (!string.IsNullOrWhiteSpace(outputPath))
    {
        return Path.GetFullPath(outputPath);
    }

    var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    var relative = Path.Combine("analytics", "detection-tuning", "output", $"threshold-report-{stamp}.json");
    return Path.GetFullPath(relative);
}

static async Task<List<SuspicionScoreRow>> LoadRowsAsync(
    string databasePath,
    CliOptions options,
    CancellationToken cancellationToken)
{
    var result = new List<SuspicionScoreRow>();
    await using var connection = new SqliteConnection($"Data Source={databasePath};Cache=Shared");
    await connection.OpenAsync(cancellationToken);
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = """
        SELECT channel, score, confidence, sample_size, account_id, match_session_id, updated_at_utc
        FROM suspicion_scores
        WHERE confidence >= $min_confidence
          AND sample_size >= $min_samples;
        """;
    cmd.Parameters.AddWithValue("$min_confidence", options.MinConfidence);
    cmd.Parameters.AddWithValue("$min_samples", options.MinSamples);

    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        var channel = reader.GetString(0);
        if (options.ChannelFilter.Count > 0 && !options.ChannelFilter.Contains(channel))
        {
            continue;
        }

        result.Add(new SuspicionScoreRow(
            Channel: channel,
            Score: reader.GetDouble(1),
            Confidence: reader.GetDouble(2),
            SampleSize: reader.GetInt32(3),
            AccountId: reader.GetString(4),
            MatchSessionId: reader.GetString(5),
            UpdatedAtUtc: DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture)));
    }

    return result;
}

static ThresholdReport BuildReport(
    string databasePath,
    CliOptions options,
    IReadOnlyList<SuspicionScoreRow> rows)
{
    var generatedAtUtc = DateTimeOffset.UtcNow;
    var grouped = rows
        .GroupBy(row => row.Channel, StringComparer.OrdinalIgnoreCase)
        .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    var channels = new List<ChannelThresholdReport>(grouped.Length);
    foreach (var group in grouped)
    {
        var scores = group.Select(item => item.Score).OrderBy(value => value).ToArray();
        if (scores.Length == 0)
        {
            continue;
        }

        var p50 = Percentile(scores, 50);
        var p90 = Percentile(scores, 90);
        var p95 = Percentile(scores, 95);
        var p99 = Percentile(scores, 99);
        var avg = scores.Average();
        var min = scores[0];
        var max = scores[^1];

        var reviewThreshold = Math.Max(65.0, p95);
        var autoThreshold = Math.Max(reviewThreshold + 8.0, p99);
        if (scores.Length < 50)
        {
            reviewThreshold = Math.Max(reviewThreshold, 80.0);
            autoThreshold = Math.Max(autoThreshold, 92.0);
        }

        reviewThreshold = Math.Round(Math.Clamp(reviewThreshold, 0.0, 99.0), 2);
        autoThreshold = Math.Round(Math.Clamp(autoThreshold, reviewThreshold + 0.5, 100.0), 2);

        var topAccounts = group
            .GroupBy(item => item.AccountId, StringComparer.Ordinal)
            .Select(accountGroup => new AccountScoreSummary(
                AccountId: accountGroup.Key,
                MaxScore: Math.Round(accountGroup.Max(item => item.Score), 2),
                AvgScore: Math.Round(accountGroup.Average(item => item.Score), 2),
                Rows: accountGroup.Count(),
                DistinctMatches: accountGroup.Select(item => item.MatchSessionId).Distinct(StringComparer.Ordinal).Count()))
            .OrderByDescending(item => item.MaxScore)
            .ThenByDescending(item => item.Rows)
            .Take(10)
            .ToArray();

        var accountsAtReview = group
            .Where(item => item.Score >= reviewThreshold)
            .Select(item => item.AccountId)
            .Distinct(StringComparer.Ordinal)
            .Count();

        var accountsAtAuto = group
            .Where(item => item.Score >= autoThreshold)
            .Select(item => item.AccountId)
            .Distinct(StringComparer.Ordinal)
            .Count();

        channels.Add(new ChannelThresholdReport(
            Channel: group.Key,
            RowCount: scores.Length,
            DistinctAccounts: group.Select(item => item.AccountId).Distinct(StringComparer.Ordinal).Count(),
            DistinctMatches: group.Select(item => item.MatchSessionId).Distinct(StringComparer.Ordinal).Count(),
            MinScore: Math.Round(min, 2),
            MaxScore: Math.Round(max, 2),
            AverageScore: Math.Round(avg, 2),
            Percentile50: Math.Round(p50, 2),
            Percentile90: Math.Round(p90, 2),
            Percentile95: Math.Round(p95, 2),
            Percentile99: Math.Round(p99, 2),
            RecommendedReviewThreshold: reviewThreshold,
            RecommendedAutoThreshold: autoThreshold,
            AccountsAtOrAboveReview: accountsAtReview,
            AccountsAtOrAboveAuto: accountsAtAuto,
            TopAccounts: topAccounts));
    }

    return new ThresholdReport(
        GeneratedAtUtc: generatedAtUtc,
        DatabasePath: databasePath,
        MinConfidence: options.MinConfidence,
        MinSamples: options.MinSamples,
        ChannelFilter: options.ChannelFilter.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
        TotalRowsAnalyzed: rows.Count,
        Channels: channels);
}

static double Percentile(IReadOnlyList<double> sortedAscending, double percentile)
{
    if (sortedAscending.Count == 0)
    {
        return 0.0;
    }

    if (sortedAscending.Count == 1)
    {
        return sortedAscending[0];
    }

    var clamped = Math.Clamp(percentile, 0.0, 100.0);
    var index = (clamped / 100.0) * (sortedAscending.Count - 1);
    var low = (int)Math.Floor(index);
    var high = (int)Math.Ceiling(index);
    if (low == high)
    {
        return sortedAscending[low];
    }

    var weight = index - low;
    return sortedAscending[low] + (sortedAscending[high] - sortedAscending[low]) * weight;
}

static void PrintUsage()
{
    Console.WriteLine("""
    ThresholdTuner.Console usage:
      dotnet run --project analytics/detection-tuning/ThresholdTuner.Console -- [options]

    Options:
      --db <path>                  SQLite path (default: src/ControlPlane.Api/data/controlplane.db)
      --out <path>                 Output report JSON path
      --min-confidence <float>     Minimum confidence filter (default: 0.60)
      --min-samples <int>          Minimum sample size filter (default: 20)
      --channels <csv>             Optional channel filter, e.g. rules,aim,trigger
      --help                       Show this help
    """);
}

internal sealed record SuspicionScoreRow(
    string Channel,
    double Score,
    double Confidence,
    int SampleSize,
    string AccountId,
    string MatchSessionId,
    DateTimeOffset UpdatedAtUtc);

internal sealed record AccountScoreSummary(
    string AccountId,
    double MaxScore,
    double AvgScore,
    int Rows,
    int DistinctMatches);

internal sealed record ChannelThresholdReport(
    string Channel,
    int RowCount,
    int DistinctAccounts,
    int DistinctMatches,
    double MinScore,
    double MaxScore,
    double AverageScore,
    double Percentile50,
    double Percentile90,
    double Percentile95,
    double Percentile99,
    double RecommendedReviewThreshold,
    double RecommendedAutoThreshold,
    int AccountsAtOrAboveReview,
    int AccountsAtOrAboveAuto,
    IReadOnlyList<AccountScoreSummary> TopAccounts);

internal sealed record ThresholdReport(
    DateTimeOffset GeneratedAtUtc,
    string DatabasePath,
    double MinConfidence,
    int MinSamples,
    IReadOnlyList<string> ChannelFilter,
    int TotalRowsAnalyzed,
    IReadOnlyList<ChannelThresholdReport> Channels);

internal sealed class CliOptions
{
    public string DatabasePath { get; private set; } = Path.Combine("src", "ControlPlane.Api", "data", "controlplane.db");
    public string? OutputPath { get; private set; }
    public double MinConfidence { get; private set; } = 0.60;
    public int MinSamples { get; private set; } = 20;
    public HashSet<string> ChannelFilter { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ShowHelp { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--db":
                    options.DatabasePath = ReadValue(args, ++i, arg);
                    break;
                case "--out":
                    options.OutputPath = ReadValue(args, ++i, arg);
                    break;
                case "--min-confidence":
                    options.MinConfidence = double.Parse(ReadValue(args, ++i, arg), CultureInfo.InvariantCulture);
                    break;
                case "--min-samples":
                    options.MinSamples = int.Parse(ReadValue(args, ++i, arg), CultureInfo.InvariantCulture);
                    break;
                case "--channels":
                    foreach (var part in ReadValue(args, ++i, arg).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        options.ChannelFilter.Add(part);
                    }

                    break;
                case "--help":
                case "-h":
                    options.ShowHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {arg}");
            }
        }

        if (options.MinConfidence < 0.0 || options.MinConfidence > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MinConfidence), "--min-confidence must be between 0 and 1.");
        }

        if (options.MinSamples < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MinSamples), "--min-samples must be >= 1.");
        }

        return options;
    }

    private static string ReadValue(string[] args, int index, string flag)
    {
        if (index >= args.Length)
        {
            throw new ArgumentException($"Missing value for {flag}");
        }

        return args[index];
    }
}
