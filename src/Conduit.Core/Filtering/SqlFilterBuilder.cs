using System;
using System.Collections.Generic;
using System.Text;
using Dapper;

namespace Conduit.Core.Models
{
    /// <summary>
    /// Converts a SCIM filter AST into a parameterized SQL WHERE clause.
    /// </summary>
    public static class SqlFilterBuilder
    {
        private static int _paramCounter;

        // User attribute path → SQL column mapping
        private static readonly Dictionary<string, string> UserColumnMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["userName"] = "u.UserName",
            ["displayName"] = "u.DisplayName",
            ["name.familyName"] = "u.FamilyName",
            ["name.givenName"] = "u.GivenName",
            ["name.middleName"] = "u.MiddleName",
            ["name.formatted"] = "u.FormattedName",
            ["name.honorificPrefix"] = "u.HonorificPrefix",
            ["name.honorificSuffix"] = "u.HonorificSuffix",
            ["title"] = "u.Title",
            ["nickName"] = "u.NickName",
            ["userType"] = "u.UserType",
            ["preferredLanguage"] = "u.PreferredLanguage",
            ["locale"] = "u.Locale",
            ["timezone"] = "u.Timezone",
            ["active"] = "u.Active",
            ["externalId"] = "u.ExternalId",
            ["id"] = "u.Id",
            ["urn:ietf:params:scim:schemas:extension:enterprise:2.0:User:department"] = "u.Department",
            ["urn:ietf:params:scim:schemas:extension:enterprise:2.0:User:employeeNumber"] = "u.EmployeeNumber",
            ["urn:ietf:params:scim:schemas:extension:enterprise:2.0:User:costCenter"] = "u.CostCenter",
            ["urn:ietf:params:scim:schemas:extension:enterprise:2.0:User:organization"] = "u.Organization",
            ["urn:ietf:params:scim:schemas:extension:enterprise:2.0:User:division"] = "u.Division",
            ["urn:ietf:params:scim:schemas:extension:enterprise:2.0:User:manager.value"] = "u.ManagerId",
        };

        // Group attribute path → SQL column mapping
        private static readonly Dictionary<string, string> GroupColumnMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["displayName"] = "g.DisplayName",
            ["id"] = "g.Id",
            ["externalId"] = "g.ExternalId",
            ["type"] = "g.Type",
        };

        // Multi-value attributes that need EXISTS subqueries (for Users)
        private static readonly Dictionary<string, (string Table, string Column)> UserSubqueryMap =
            new(StringComparer.OrdinalIgnoreCase)
        {
            ["emails.value"] = ("UserEmails", "Value"),
            ["emails.type"] = ("UserEmails", "Type"),
            ["phoneNumbers.value"] = ("UserPhoneNumbers", "Value"),
            ["phoneNumbers.type"] = ("UserPhoneNumbers", "Type"),
        };

        // Multi-value attributes for Groups
        private static readonly Dictionary<string, (string Table, string Column)> GroupSubqueryMap =
            new(StringComparer.OrdinalIgnoreCase)
        {
            ["members.value"] = ("GroupMembers", "Value"),
            ["members.type"] = ("GroupMembers", "Type"),
        };

        /// <summary>
        /// Builds a parameterized SQL WHERE clause from a filter AST.
        /// </summary>
        /// <param name="node">The filter AST root node</param>
        /// <param name="resourceType">"User" or "Group"</param>
        /// <returns>SQL WHERE clause (without "WHERE") and Dapper parameters</returns>
        public static (string Sql, DynamicParameters Parameters) Build(ScimFilterNode node, string resourceType)
        {
            _paramCounter = 0;
            var parameters = new DynamicParameters();
            var sql = BuildNode(node, resourceType, parameters);
            return (sql, parameters);
        }

        private static string BuildNode(ScimFilterNode node, string resourceType, DynamicParameters parameters)
        {
            return node switch
            {
                ComparisonNode comp => BuildComparison(comp, resourceType, parameters),
                PresentNode pr => BuildPresent(pr, resourceType),
                LogicalNode logical => BuildLogical(logical, resourceType, parameters),
                _ => throw new ScimFilterParseException($"Unknown filter node type: {node.GetType().Name}")
            };
        }

        private static string BuildLogical(LogicalNode node, string resourceType, DynamicParameters parameters)
        {
            var left = BuildNode(node.Left, resourceType, parameters);
            var right = BuildNode(node.Right, resourceType, parameters);
            var op = node.Operator.ToUpperInvariant(); // AND / OR
            return $"({left} {op} {right})";
        }

        private static string BuildPresent(PresentNode node, string resourceType)
        {
            var subqueryMap = resourceType == "Group" ? GroupSubqueryMap : UserSubqueryMap;

            if (subqueryMap.TryGetValue(node.AttributePath, out var sub))
            {
                var fk = resourceType == "Group" ? "GroupId" : "UserId";
                var pk = resourceType == "Group" ? "g.Id" : "u.Id";
                return $"EXISTS (SELECT 1 FROM {sub.Table} WHERE {fk} = {pk})";
            }

            var column = ResolveColumn(node.AttributePath, resourceType);
            return $"{column} IS NOT NULL";
        }

        private static string BuildComparison(ComparisonNode node, string resourceType, DynamicParameters parameters)
        {
            var paramName = $"@p{_paramCounter++}";
            var subqueryMap = resourceType == "Group" ? GroupSubqueryMap : UserSubqueryMap;

            // Handle multi-value attributes via EXISTS subquery
            if (subqueryMap.TryGetValue(node.AttributePath, out var sub))
            {
                var fk = resourceType == "Group" ? "GroupId" : "UserId";
                var pk = resourceType == "Group" ? "g.Id" : "u.Id";
                var sqlOp = GetSqlOperator(node.Operator, sub.Column, paramName);
                parameters.Add(paramName, CoerceValue(node.Value, node.AttributePath));
                return $"EXISTS (SELECT 1 FROM {sub.Table} WHERE {fk} = {pk} AND {sqlOp})";
            }

            var column = ResolveColumn(node.AttributePath, resourceType);

            // Special handling for boolean active field
            if (node.AttributePath.Equals("active", StringComparison.OrdinalIgnoreCase))
            {
                var boolVal = node.Value?.ToLowerInvariant() == "true" ? 1 : 0;
                parameters.Add(paramName, boolVal);
                return $"{column} {GetSimpleOperator(node.Operator)} {paramName}";
            }

            parameters.Add(paramName, CoerceValue(node.Value, node.AttributePath));

            // For contains/startsWith/endsWith, use LIKE
            switch (node.Operator)
            {
                case "co":
                    parameters.Add(paramName, $"%{EscapeLike(node.Value ?? "")}%");
                    return $"{column} LIKE {paramName}";
                case "sw":
                    parameters.Add(paramName, $"{EscapeLike(node.Value ?? "")}%");
                    return $"{column} LIKE {paramName}";
                case "ew":
                    parameters.Add(paramName, $"%{EscapeLike(node.Value ?? "")}");
                    return $"{column} LIKE {paramName}";
                default:
                    return $"{column} {GetSimpleOperator(node.Operator)} {paramName}";
            }
        }

        private static string GetSqlOperator(string op, string column, string paramName)
        {
            return op switch
            {
                "co" => $"{column} LIKE {paramName}",
                "sw" => $"{column} LIKE {paramName}",
                "ew" => $"{column} LIKE {paramName}",
                _ => $"{column} {GetSimpleOperator(op)} {paramName}"
            };
        }

        private static string GetSimpleOperator(string op)
        {
            return op switch
            {
                "eq" => "=",
                "ne" => "!=",
                "gt" => ">",
                "ge" => ">=",
                "lt" => "<",
                "le" => "<=",
                _ => "="
            };
        }

        private static string ResolveColumn(string attributePath, string resourceType)
        {
            var columnMap = resourceType == "Group" ? GroupColumnMap : UserColumnMap;
            if (columnMap.TryGetValue(attributePath, out var column))
                return column;

            throw new ScimFilterParseException($"Unsupported filter attribute: '{attributePath}'");
        }

        private static object? CoerceValue(string? value, string attributePath)
        {
            if (value == null) return null;

            if (attributePath.Equals("active", StringComparison.OrdinalIgnoreCase))
                return value.ToLowerInvariant() == "true" ? 1 : 0;

            // GUID columns
            if (attributePath.Equals("id", StringComparison.OrdinalIgnoreCase) ||
                attributePath.EndsWith(".value", StringComparison.OrdinalIgnoreCase))
            {
                if (Guid.TryParse(value, out var guid))
                    return guid;
            }

            return value;
        }

        private static string EscapeLike(string value)
        {
            return value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
        }
    }
}
