using System;
using System.Collections.Generic;

// Published migration v1. Keep this file immutable; use a new migration for repairs.
public static class AddCategoryV2
{
    public static void Apply(IList<Dictionary<string, object>> records)
    {
        foreach (var record in records) {
            if (record.ContainsKey("category") || record.ContainsKey("category_missing_reason"))
                throw new InvalidOperationException("DS1 must not already contain DS2 fields.");
            record.Add("category", null);
            record.Add("category_missing_reason", "not_collected");
        }
    }
}
