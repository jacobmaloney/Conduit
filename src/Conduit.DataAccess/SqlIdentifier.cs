namespace Conduit.DataAccess
{
    /// <summary>
    /// Bracket-quoting for SQL identifiers that cannot be parameterized — DDL like
    /// <c>CREATE DATABASE</c> takes a name, not a value, so the name is concatenated into
    /// the statement and the escape is the only thing standing between an operator-supplied
    /// database name and injected DDL.
    ///
    /// One spelling, one place. This existed twice — <c>DatabaseInitializer</c> unescaped
    /// and <c>SetupService</c> escaped — which is exactly how the unescaped copy survived:
    /// fixing one says nothing about the other.
    /// </summary>
    public static class SqlIdentifier
    {
        /// <summary>
        /// Wraps an identifier in brackets, doubling any embedded <c>]</c> so it cannot
        /// close the bracket early. Mirrors T-SQL <c>QUOTENAME()</c>.
        /// </summary>
        public static string QuoteName(string identifier) =>
            "[" + (identifier ?? string.Empty).Replace("]", "]]") + "]";
    }
}
