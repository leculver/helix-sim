namespace QueueSim;

class QueueHistory
{
    public DateTime Timestamp { get; set; }
    public string QueueName { get; set; } = "";
    public double[] Durations { get; set; } = [];

    public static Dictionary<string, QueueHistory[]> Load(string path)
    {
        var query = from line in File.ReadLines(path).Skip(1)
                    let queue = LoadFromLine(line)
                    orderby queue.Timestamp
                    group queue by queue.QueueName into g
                    select new { QueueName = g.Key, Queue = g.ToArray() };

        return query.ToDictionary(x => x.QueueName, x => x.Queue);
    }

    private static QueueHistory LoadFromLine(string line)
    {
        string[] parts = line.Split('\t');
        double[] durations = [.. parts[3].Split(' ').Select(double.Parse)];
        DateTime timestamp = DateTime.Parse(parts[0]);
        return new QueueHistory
        {
            Timestamp = timestamp,
            QueueName = parts[1],
            Durations = durations
        };
    }
}
