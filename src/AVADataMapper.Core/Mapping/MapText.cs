namespace AVADataMapper.Core.Mapping;

/// <summary>A mapping in words, for people: the window, the reports and agents say a column map the same way.</summary>
public static class MapText
{
    /// <summary>A column map in words: "split PatName at ',', part 1, trimmed".</summary>
    public static string Describe(ColumnMap c)
    {
        var s = c.Kind switch
        {
            ColumnKind.Direct => $"{c.From}",
            ColumnKind.Convert => $"{c.From} as {c.As}" + (c.Formats is { Count: > 0 } f ? $" ({string.Join(", ", f)})" : "") + (c.Map is { Count: > 0 } m ? $" ({string.Join(", ", m.Select(kv => $"{kv.Key} → {kv.Value?.ToJsonString().Trim('"')}"))})" : ""),
            ColumnKind.Lookup => $"{c.From} looked up" + (c.Map is { Count: > 0 } m ? $" ({string.Join(", ", m.Take(6).Select(kv => $"{kv.Key} → {kv.Value?.ToJsonString().Trim('"')}"))}{(m.Count > 6 ? "…" : "")})" : "") + (c.Table != null ? $" in {c.Table}" : ""),
            ColumnKind.Key => $"the new key of {c.Table} row whose old key is {c.From}",
            ColumnKind.Split => $"{c.From} split at '{c.Separator}', part {c.Part}",
            ColumnKind.Merge => $"{string.Join(" + ", c.Froms ?? [])} joined with '{c.Separator}'",
            ColumnKind.Constant => $"always {c.Value?.ToJsonString()}",
            ColumnKind.Default => "the new column's default",
            ColumnKind.Expression => c.Expression ?? "",
            _ => "numbered by the new database",
        };
        var extras = new List<string>();
        if (c.Trim == true) extras.Add("trimmed");
        if (c.Case != null) extras.Add(c.Case + " case");
        if (c.BlankIsNull == true) extras.Add("blank is null");
        if (c.IfNull != null) extras.Add($"null → {c.IfNull.ToJsonString()}");
        if (c.Unknown != null) extras.Add($"else {c.Unknown}");
        if (c.Certainty is Certainty.Sure or Certainty.Likely or Certainty.Guessed) extras.Add($"suggested, {c.Certainty.ToString().ToLowerInvariant()}");
        return s + (extras.Count > 0 ? "; " + string.Join(", ", extras) : "");
    }
}
