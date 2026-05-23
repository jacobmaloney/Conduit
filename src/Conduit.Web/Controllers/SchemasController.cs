using System.Collections.Generic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Conduit.Web.Controllers
{
    /// <summary>
    /// SCIM Schemas discovery endpoint
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [Produces("application/scim+json")]
    [Route("scim/v2/Schemas")]
    [Route("scim/v2/t/{slug}/Schemas")]
    [EnableRateLimiting("anon")]
    public class SchemasController : ControllerBase
    {
        private const string UserSchemaId = "urn:ietf:params:scim:schemas:core:2.0:User";
        private const string GroupSchemaId = "urn:ietf:params:scim:schemas:core:2.0:Group";
        private const string EnterpriseUserSchemaId = "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User";

        [HttpGet]
        public IActionResult GetSchemas()
        {
            var schemas = new List<object> { BuildUserSchema(), BuildGroupSchema(), BuildEnterpriseUserSchema() };

            var response = new
            {
                schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:ListResponse" },
                totalResults = schemas.Count,
                itemsPerPage = schemas.Count,
                startIndex = 1,
                Resources = schemas
            };

            return Ok(response);
        }

        [HttpGet("{schemaId}")]
        public IActionResult GetSchema(string schemaId)
        {
            // URL-decode the schemaId since URNs contain colons
            schemaId = System.Uri.UnescapeDataString(schemaId);

            return schemaId switch
            {
                UserSchemaId => Ok(BuildUserSchema()),
                GroupSchemaId => Ok(BuildGroupSchema()),
                EnterpriseUserSchemaId => Ok(BuildEnterpriseUserSchema()),
                _ => NotFound(new { schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:Error" }, status = 404, detail = $"Schema '{schemaId}' not found" })
            };
        }

        private object BuildUserSchema()
        {
            return new
            {
                id = UserSchemaId,
                name = "User",
                description = "User Account",
                attributes = new object[]
                {
                    Attr("userName", "string", "readWrite", true, "Unique identifier for the User"),
                    ComplexAttr("name", "readWrite", false, "The components of the user's real name", new object[]
                    {
                        Attr("formatted", "string", "readWrite", false, "Full name"),
                        Attr("familyName", "string", "readWrite", false, "Family name"),
                        Attr("givenName", "string", "readWrite", false, "Given name"),
                        Attr("middleName", "string", "readWrite", false, "Middle name"),
                        Attr("honorificPrefix", "string", "readWrite", false, "Honorific prefix"),
                        Attr("honorificSuffix", "string", "readWrite", false, "Honorific suffix")
                    }),
                    Attr("displayName", "string", "readWrite", false, "Display name"),
                    Attr("nickName", "string", "readWrite", false, "Casual name"),
                    Attr("profileUrl", "reference", "readWrite", false, "URL of profile"),
                    Attr("title", "string", "readWrite", false, "Title"),
                    Attr("userType", "string", "readWrite", false, "Relationship to organization"),
                    Attr("preferredLanguage", "string", "readWrite", false, "Preferred language tag"),
                    Attr("locale", "string", "readWrite", false, "Default location"),
                    Attr("timezone", "string", "readWrite", false, "Time zone"),
                    Attr("active", "boolean", "readWrite", false, "Administrative status"),
                    Attr("password", "string", "writeOnly", false, "Password"),
                    MultiValueAttr("emails", "readWrite", new object[]
                    {
                        Attr("value", "string", "readWrite", false, "Email address"),
                        Attr("display", "string", "readWrite", false, "Display value"),
                        Attr("type", "string", "readWrite", false, "Type (work, home, other)"),
                        Attr("primary", "boolean", "readWrite", false, "Primary indicator")
                    }),
                    MultiValueAttr("phoneNumbers", "readWrite", new object[]
                    {
                        Attr("value", "string", "readWrite", false, "Phone number"),
                        Attr("display", "string", "readWrite", false, "Display value"),
                        Attr("type", "string", "readWrite", false, "Type (work, home, mobile, fax, pager, other)"),
                        Attr("primary", "boolean", "readWrite", false, "Primary indicator")
                    }),
                    MultiValueAttr("addresses", "readWrite", new object[]
                    {
                        Attr("formatted", "string", "readWrite", false, "Full mailing address"),
                        Attr("streetAddress", "string", "readWrite", false, "Street address"),
                        Attr("locality", "string", "readWrite", false, "City or locality"),
                        Attr("region", "string", "readWrite", false, "State or region"),
                        Attr("postalCode", "string", "readWrite", false, "Postal code"),
                        Attr("country", "string", "readWrite", false, "Country"),
                        Attr("type", "string", "readWrite", false, "Type (work, home, other)"),
                        Attr("primary", "boolean", "readWrite", false, "Primary indicator")
                    }),
                    MultiValueAttr("groups", "readOnly", new object[]
                    {
                        Attr("value", "string", "readOnly", false, "Group ID"),
                        Attr("$ref", "reference", "readOnly", false, "Group URI"),
                        Attr("display", "string", "readOnly", false, "Group display name"),
                        Attr("type", "string", "readOnly", false, "Type (direct, indirect)")
                    }),
                    MultiValueAttr("roles", "readWrite", new object[]
                    {
                        Attr("value", "string", "readWrite", false, "Role value"),
                        Attr("display", "string", "readWrite", false, "Display value"),
                        Attr("type", "string", "readWrite", false, "Type"),
                        Attr("primary", "boolean", "readWrite", false, "Primary indicator")
                    })
                },
                meta = new
                {
                    resourceType = "Schema",
                    location = $"/scim/v2/Schemas/{UserSchemaId}"
                }
            };
        }

        private object BuildGroupSchema()
        {
            return new
            {
                id = GroupSchemaId,
                name = "Group",
                description = "Group",
                attributes = new object[]
                {
                    Attr("displayName", "string", "readWrite", true, "Human-readable name"),
                    MultiValueAttr("members", "readWrite", new object[]
                    {
                        Attr("value", "string", "immutable", false, "Member ID"),
                        Attr("$ref", "reference", "immutable", false, "Member URI"),
                        Attr("type", "string", "immutable", false, "Member type (User or Group)"),
                        Attr("display", "string", "readOnly", false, "Member display name")
                    })
                },
                meta = new
                {
                    resourceType = "Schema",
                    location = $"/scim/v2/Schemas/{GroupSchemaId}"
                }
            };
        }

        private object BuildEnterpriseUserSchema()
        {
            return new
            {
                id = EnterpriseUserSchemaId,
                name = "EnterpriseUser",
                description = "Enterprise User extension",
                attributes = new object[]
                {
                    Attr("employeeNumber", "string", "readWrite", false, "Employee number"),
                    Attr("costCenter", "string", "readWrite", false, "Cost center"),
                    Attr("organization", "string", "readWrite", false, "Organization"),
                    Attr("division", "string", "readWrite", false, "Division"),
                    Attr("department", "string", "readWrite", false, "Department"),
                    ComplexAttr("manager", "readWrite", false, "Manager", new object[]
                    {
                        Attr("value", "string", "readWrite", false, "Manager user ID"),
                        Attr("$ref", "reference", "readWrite", false, "Manager URI"),
                        Attr("displayName", "string", "readOnly", false, "Manager display name")
                    })
                },
                meta = new
                {
                    resourceType = "Schema",
                    location = $"/scim/v2/Schemas/{EnterpriseUserSchemaId}"
                }
            };
        }

        private static object Attr(string name, string type, string mutability, bool required, string description)
        {
            return new { name, type, multiValued = false, description, required, mutability, returned = "default" };
        }

        private static object ComplexAttr(string name, string mutability, bool required, string description, object[] subAttributes)
        {
            return new { name, type = "complex", multiValued = false, description, required, mutability, returned = "default", subAttributes };
        }

        private static object MultiValueAttr(string name, string mutability, object[] subAttributes)
        {
            return new { name, type = "complex", multiValued = true, description = name, required = false, mutability, returned = "default", subAttributes };
        }
    }
}
