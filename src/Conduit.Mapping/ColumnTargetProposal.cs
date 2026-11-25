namespace Conduit.Mapping;

/// <summary>
/// Proposes which target field each column of a file fills, from the columns the file really has.
/// A column is matched when its name, with case, spaces and punctuation ignored, is a target field or a
/// known alias of one; nothing is matched on a partial name, so "ManagerEmployeeId" is never taken for
/// "EmployeeId". Each target is filled once, by the first column that matches it. Host-neutral: the
/// targets are whatever the destination says it accepts.
/// </summary>
public static class ColumnTargetProposal
{
    public sealed record Row(string Column, string Target);

    /// <summary>The proposed rows in file order, and the columns no target was found for.</summary>
    public sealed record Result(IReadOnlyList<Row> Rows, IReadOnlyList<string> Unmapped);

    /// <summary>A name with case, spaces and punctuation removed, so "Employee ID" and "employee_id" compare equal.</summary>
    public static string Normalize(string? name)
    {
        var text = name ?? string.Empty;
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>
    /// Common HR column names for a person record: normalized column name to person field. An alias only
    /// applies when the destination accepts that field.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> PersonAliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Normalized target field names (exact self-matches)
        ["employeeid"] = "EmployeeId",
        ["firstname"] = "FirstName",
        ["lastname"] = "LastName",
        ["displayname"] = "DisplayName",
        ["email"] = "PrimaryEmail",
        ["phone"] = "PrimaryPhone",
        ["mobilephone"] = "MobilePhone",
        ["department"] = "Department",
        ["jobtitle"] = "JobTitle",
        ["company"] = "Company",
        ["office"] = "Office",
        ["division"] = "Division",
        ["username"] = "Username",
        ["userprincipalname"] = "UserPrincipalName",
        ["streetaddress"] = "StreetAddress",
        ["city"] = "City",
        ["state"] = "State",
        ["postalcode"] = "PostalCode",
        ["country"] = "Country",
        ["manageremployeeid"] = "ManagerEmployeeId",
        ["hiredate"] = "HireDate",
        ["terminationdate"] = "TerminationDate",
        ["dateofbirth"] = "DateOfBirth",
        ["lastworkday"] = "LastWorkDay",
        ["costcenter"] = "CostCenter",
        ["employeetype"] = "EmployeeType",
        ["middlename"] = "MiddleName",
        ["description"] = "Description",
        // Short abbreviations that fuzzy matching can't resolve
        ["sn"] = "LastName",
        ["fname"] = "FirstName",
        ["lname"] = "LastName",
        ["empid"] = "EmployeeId",
        ["dept"] = "Department",
        ["dob"] = "DateOfBirth",
        ["mgr"] = "ManagerEmployeeId",
        ["upn"] = "UserPrincipalName",
        ["mail"] = "PrimaryEmail",
        ["st"] = "State",
        ["zip"] = "PostalCode",
        // Common variations (fuzzy handles most, but exact alias is faster)
        ["givenname"] = "FirstName",
        ["surname"] = "LastName",
        ["familyname"] = "LastName",
        ["employeenumber"] = "EmployeeId",
        ["fullname"] = "DisplayName",
        ["name"] = "DisplayName",
        ["emailaddress"] = "PrimaryEmail",
        ["primaryemail"] = "PrimaryEmail",
        ["phonenumber"] = "PrimaryPhone",
        ["telephone"] = "PrimaryPhone",
        ["primaryphone"] = "PrimaryPhone",
        ["mobile"] = "MobilePhone",
        ["cellphone"] = "MobilePhone",
        ["cell"] = "MobilePhone",
        ["title"] = "JobTitle",
        ["position"] = "JobTitle",
        ["organization"] = "Company",
        ["org"] = "Company",
        ["officelocation"] = "Office",
        ["physicaldeliveryofficename"] = "Office",
        ["samaccountname"] = "Username",
        ["login"] = "Username",
        ["userid"] = "Username",
        ["address"] = "StreetAddress",
        ["street"] = "StreetAddress",
        ["locality"] = "City",
        ["stateorprovince"] = "State",
        ["zipcode"] = "PostalCode",
        ["countrycode"] = "Country",
        ["managerid"] = "ManagerEmployeeId",
        ["startdate"] = "StartDate",
        ["enddate"] = "EndDate",
    };

    /// <summary>
    /// A column that also fills a second person field when no column fills that field itself. A file has
    /// one "employee type" column; the person record keeps both the plain attribute and the type people
    /// are classified by.
    /// </summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> PersonAlsoFills = new[]
    {
        new KeyValuePair<string, string>("employeetype", "IdentityType"),
    };

    public static Result Propose(IEnumerable<string>? columns, IEnumerable<string>? targets,
        IReadOnlyDictionary<string, string>? aliases = null, IEnumerable<KeyValuePair<string, string>>? alsoFills = null)
    {
        var rows = new List<Row>();
        var unmapped = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byNormalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in targets ?? Array.Empty<string>())
        {
            var name = (t ?? string.Empty).Trim();
            if (name.Length > 0) byNormalized.TryAdd(Normalize(name), name);
        }

        var seen = new List<(string Column, string Normalized)>();
        foreach (var raw in columns ?? Array.Empty<string>())
        {
            var column = (raw ?? string.Empty).Trim();
            if (column.Length == 0) continue;
            var normalized = Normalize(column);
            seen.Add((column, normalized));
            if (!byNormalized.TryGetValue(normalized, out var target)
                && !(aliases != null && aliases.TryGetValue(normalized, out var alias) && byNormalized.TryGetValue(Normalize(alias), out target)))
            {
                unmapped.Add(column);
                continue;
            }
            if (!used.Add(target!))
            {
                unmapped.Add(column);
                continue;
            }
            rows.Add(new Row(column, target!));
        }

        foreach (var also in alsoFills ?? Array.Empty<KeyValuePair<string, string>>())
        {
            if (!byNormalized.TryGetValue(Normalize(also.Value), out var extra) || used.Contains(extra)) continue;
            var from = seen.FirstOrDefault(c => c.Normalized == also.Key);
            if (from.Column is null || !rows.Any(r => r.Column == from.Column)) continue;
            used.Add(extra);
            rows.Add(new Row(from.Column, extra));
        }
        return new Result(rows, unmapped);
    }
}
