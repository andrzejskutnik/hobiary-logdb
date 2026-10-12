using System.Text;
using HobiaryLogDB.Tests;

if (args.Length != 0)
{
    Console.Error.WriteLine("Usage: dotnet run --project samples/LogDemo");
    return 1;
}

var directory = Path.Combine(Path.GetTempPath(), $"hobiary-logdb-demo-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
var expected = new[]
{
    new LogRecord(HlogLevel.Info, timestamp, "Orders", "portfolio-demo", Encoding.UTF8.GetBytes("Order 42 created")),
    new LogRecord(HlogLevel.Warn, timestamp + 1000, "Inventory", "portfolio-demo", Encoding.UTF8.GetBytes("Stock is running low")),
    new LogRecord(HlogLevel.Error, timestamp + 2000, "Payments", "portfolio-demo", Encoding.UTF8.GetBytes("Synthetic payment failure")),
};

using (var client = new HobiaryLogClient(directory))
{
    foreach (var record in expected)
    {
        var result = client.Append(record);
        if (result != 0)
            throw new InvalidOperationException($"Append failed with native error {result}.");
    }
}

using (var client = new HobiaryLogClient(directory))
{
    var records = new List<LogRecord>();
    var count = client.VisitRange(timestamp, timestamp + 2000, record =>
    {
        records.Add(record);
        return true;
    });
    if (count != expected.Length || records.Count != expected.Length)
        throw new InvalidOperationException($"Readback failed: expected 3 records, native result {count}, received {records.Count}.");

    for (var i = 0; i < expected.Length; i++)
    {
        var actual = records[i];
        if (actual.Level != expected[i].Level || actual.Timestamp != expected[i].Timestamp
            || actual.Source != expected[i].Source || actual.Service != expected[i].Service
            || actual.Payload is null || !actual.Payload.SequenceEqual(expected[i].Payload!))
            throw new InvalidOperationException($"Readback mismatch at record {i}.");
        Console.WriteLine($"[{actual.Level}] {actual.Source}: {Encoding.UTF8.GetString(actual.Payload)}");
    }

    var errors = client.VisitLevel(HlogLevel.Error, record => record.Source == "Payments");
    if (errors != 1)
        throw new InvalidOperationException($"Error index returned {errors} records instead of 1.");
    Console.WriteLine("PASS: reopened log contains 3 matching records; error index returned 1 record.");
}

Console.WriteLine($"Inspect the generated .hlog and .hidx files in: {directory}");
return 0;
