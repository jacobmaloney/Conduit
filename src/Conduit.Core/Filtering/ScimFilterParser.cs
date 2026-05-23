using System;
using System.Collections.Generic;

namespace Conduit.Core.Models
{
    /// <summary>
    /// Recursive descent parser for SCIM 2.0 filter expressions.
    /// Supports: eq, ne, co, sw, ew, pr, gt, ge, lt, le, and, or, parentheses.
    /// </summary>
    public static class ScimFilterParser
    {
        private static readonly HashSet<string> ComparisonOperators = new(StringComparer.OrdinalIgnoreCase)
        {
            "eq", "ne", "co", "sw", "ew", "gt", "ge", "lt", "le"
        };

        public static ScimFilterNode Parse(string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
                throw new ScimFilterParseException("Filter expression is empty");

            var tokens = Tokenize(filter);
            int pos = 0;
            var result = ParseOrExpression(tokens, ref pos);

            if (pos < tokens.Count)
                throw new ScimFilterParseException($"Unexpected token at position {pos}: '{tokens[pos]}'");

            return result;
        }

        private static ScimFilterNode ParseOrExpression(List<string> tokens, ref int pos)
        {
            var left = ParseAndExpression(tokens, ref pos);

            while (pos < tokens.Count && string.Equals(tokens[pos], "or", StringComparison.OrdinalIgnoreCase))
            {
                pos++; // consume "or"
                var right = ParseAndExpression(tokens, ref pos);
                left = new LogicalNode { Operator = "or", Left = left, Right = right };
            }

            return left;
        }

        private static ScimFilterNode ParseAndExpression(List<string> tokens, ref int pos)
        {
            var left = ParseAtom(tokens, ref pos);

            while (pos < tokens.Count && string.Equals(tokens[pos], "and", StringComparison.OrdinalIgnoreCase))
            {
                pos++; // consume "and"
                var right = ParseAtom(tokens, ref pos);
                left = new LogicalNode { Operator = "and", Left = left, Right = right };
            }

            return left;
        }

        private static ScimFilterNode ParseAtom(List<string> tokens, ref int pos)
        {
            if (pos >= tokens.Count)
                throw new ScimFilterParseException("Unexpected end of filter expression");

            // Parenthesized expression
            if (tokens[pos] == "(")
            {
                pos++; // consume "("
                var node = ParseOrExpression(tokens, ref pos);
                if (pos >= tokens.Count || tokens[pos] != ")")
                    throw new ScimFilterParseException("Missing closing parenthesis");
                pos++; // consume ")"
                return node;
            }

            // Must be an attribute path
            var attribute = tokens[pos];
            pos++;

            if (pos >= tokens.Count)
                throw new ScimFilterParseException($"Expected operator after '{attribute}'");

            var op = tokens[pos];

            // Present operator
            if (string.Equals(op, "pr", StringComparison.OrdinalIgnoreCase))
            {
                pos++; // consume "pr"
                return new PresentNode { AttributePath = attribute };
            }

            // Comparison operator
            if (!ComparisonOperators.Contains(op))
                throw new ScimFilterParseException($"Unknown operator '{op}'");

            pos++; // consume operator

            if (pos >= tokens.Count)
                throw new ScimFilterParseException($"Expected value after '{attribute} {op}'");

            var value = tokens[pos];
            pos++; // consume value

            // Strip surrounding quotes if present
            if (value.Length >= 2 && value.StartsWith("\"") && value.EndsWith("\""))
            {
                value = value.Substring(1, value.Length - 2);
            }

            return new ComparisonNode
            {
                AttributePath = attribute,
                Operator = op.ToLowerInvariant(),
                Value = value
            };
        }

        /// <summary>
        /// Tokenizes a SCIM filter string into tokens.
        /// Handles quoted strings, parentheses, and whitespace-delimited tokens.
        /// </summary>
        private static List<string> Tokenize(string filter)
        {
            var tokens = new List<string>();
            int i = 0;

            while (i < filter.Length)
            {
                // Skip whitespace
                if (char.IsWhiteSpace(filter[i]))
                {
                    i++;
                    continue;
                }

                // Parentheses
                if (filter[i] == '(' || filter[i] == ')')
                {
                    tokens.Add(filter[i].ToString());
                    i++;
                    continue;
                }

                // Quoted string
                if (filter[i] == '"')
                {
                    int start = i;
                    i++; // skip opening quote
                    while (i < filter.Length && filter[i] != '"')
                    {
                        if (filter[i] == '\\' && i + 1 < filter.Length)
                            i++; // skip escaped character
                        i++;
                    }
                    if (i < filter.Length)
                        i++; // skip closing quote
                    tokens.Add(filter.Substring(start, i - start));
                    continue;
                }

                // Unquoted token (attribute, operator, or bare value)
                {
                    int start = i;
                    while (i < filter.Length && !char.IsWhiteSpace(filter[i]) && filter[i] != '(' && filter[i] != ')')
                    {
                        i++;
                    }
                    tokens.Add(filter.Substring(start, i - start));
                }
            }

            return tokens;
        }
    }
}
