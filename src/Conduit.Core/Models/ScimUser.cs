using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Conduit.Core.Models
{
    /// <summary>
    /// SCIM User resource
    /// </summary>
    public class ScimUser : ScimResource
    {
        /// <summary>
        /// Initializes a new instance of the ScimUser class
        /// </summary>
        public ScimUser()
        {
            Schemas.Add("urn:ietf:params:scim:schemas:core:2.0:User");
            Meta.ResourceType = "User";
        }

        /// <summary>
        /// Gets or sets the username
        /// </summary>
        [JsonProperty("userName")]
        public string UserName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the user's name
        /// </summary>
        [JsonProperty("name")]
        public ScimName? Name { get; set; }

        /// <summary>
        /// Gets or sets the display name
        /// </summary>
        [JsonProperty("displayName")]
        public string? DisplayName { get; set; }

        /// <summary>
        /// Gets or sets the nick name
        /// </summary>
        [JsonProperty("nickName")]
        public string? NickName { get; set; }

        /// <summary>
        /// Gets or sets the profile URL
        /// </summary>
        [JsonProperty("profileUrl")]
        public string? ProfileUrl { get; set; }

        /// <summary>
        /// Gets or sets the user's title
        /// </summary>
        [JsonProperty("title")]
        public string? Title { get; set; }

        /// <summary>
        /// Gets or sets the user type
        /// </summary>
        [JsonProperty("userType")]
        public string? UserType { get; set; }

        /// <summary>
        /// Gets or sets the preferred language
        /// </summary>
        [JsonProperty("preferredLanguage")]
        public string? PreferredLanguage { get; set; }

        /// <summary>
        /// Gets or sets the locale
        /// </summary>
        [JsonProperty("locale")]
        public string? Locale { get; set; }

        /// <summary>
        /// Gets or sets the timezone
        /// </summary>
        [JsonProperty("timezone")]
        public string? Timezone { get; set; }

        /// <summary>
        /// Gets or sets whether the user is active
        /// </summary>
        [JsonProperty("active")]
        public bool Active { get; set; } = true;

        /// <summary>
        /// Gets or sets the password (write-only)
        /// </summary>
        [JsonProperty("password", NullValueHandling = NullValueHandling.Ignore)]
        public string? Password { get; set; }

        /// <summary>
        /// Gets or sets the user's emails
        /// </summary>
        [JsonProperty("emails")]
        public List<ScimEmail> Emails { get; set; } = new List<ScimEmail>();

        /// <summary>
        /// Gets or sets the user's phone numbers
        /// </summary>
        [JsonProperty("phoneNumbers")]
        public List<ScimPhoneNumber> PhoneNumbers { get; set; } = new List<ScimPhoneNumber>();

        /// <summary>
        /// Gets or sets the user's instant messaging addresses
        /// </summary>
        [JsonProperty("ims")]
        public List<ScimIms> Ims { get; set; } = new List<ScimIms>();

        /// <summary>
        /// Gets or sets the user's photos
        /// </summary>
        [JsonProperty("photos")]
        public List<ScimPhoto> Photos { get; set; } = new List<ScimPhoto>();

        /// <summary>
        /// Gets or sets the user's addresses
        /// </summary>
        [JsonProperty("addresses")]
        public List<ScimAddress> Addresses { get; set; } = new List<ScimAddress>();

        /// <summary>
        /// Gets or sets the user's groups
        /// </summary>
        [JsonProperty("groups")]
        public List<ScimGroupRef> Groups { get; set; } = new List<ScimGroupRef>();

        /// <summary>
        /// Gets or sets the user's entitlements
        /// </summary>
        [JsonProperty("entitlements")]
        public List<ScimEntitlement> Entitlements { get; set; } = new List<ScimEntitlement>();

        /// <summary>
        /// Gets or sets the user's roles
        /// </summary>
        [JsonProperty("roles")]
        public List<ScimRole> Roles { get; set; } = new List<ScimRole>();

        /// <summary>
        /// Gets or sets the user's X.509 certificates
        /// </summary>
        [JsonProperty("x509Certificates")]
        public List<ScimCertificate> X509Certificates { get; set; } = new List<ScimCertificate>();

        /// <summary>
        /// Gets or sets the enterprise user extension
        /// </summary>
        [JsonProperty("urn:ietf:params:scim:schemas:extension:enterprise:2.0:User")]
        public ScimEnterpriseUser? EnterpriseExtension { get; set; }
    }

    /// <summary>
    /// SCIM name complex type
    /// </summary>
    public class ScimName
    {
        /// <summary>
        /// Gets or sets the formatted full name
        /// </summary>
        [JsonProperty("formatted")]
        public string? Formatted { get; set; }

        /// <summary>
        /// Gets or sets the family name
        /// </summary>
        [JsonProperty("familyName")]
        public string? FamilyName { get; set; }

        /// <summary>
        /// Gets or sets the given name
        /// </summary>
        [JsonProperty("givenName")]
        public string? GivenName { get; set; }

        /// <summary>
        /// Gets or sets the middle name
        /// </summary>
        [JsonProperty("middleName")]
        public string? MiddleName { get; set; }

        /// <summary>
        /// Gets or sets the honorific prefix
        /// </summary>
        [JsonProperty("honorificPrefix")]
        public string? HonorificPrefix { get; set; }

        /// <summary>
        /// Gets or sets the honorific suffix
        /// </summary>
        [JsonProperty("honorificSuffix")]
        public string? HonorificSuffix { get; set; }
    }

    /// <summary>
    /// SCIM email complex type
    /// </summary>
    public class ScimEmail
    {
        /// <summary>
        /// Gets or sets the email value
        /// </summary>
        [JsonProperty("value")]
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the email type
        /// </summary>
        [JsonProperty("type")]
        public string? Type { get; set; }

        /// <summary>
        /// Gets or sets whether this is the primary email
        /// </summary>
        [JsonProperty("primary")]
        public bool Primary { get; set; }

        /// <summary>
        /// Gets or sets the display value
        /// </summary>
        [JsonProperty("display")]
        public string? Display { get; set; }
    }

    /// <summary>
    /// SCIM phone number complex type
    /// </summary>
    public class ScimPhoneNumber
    {
        /// <summary>
        /// Gets or sets the phone number value
        /// </summary>
        [JsonProperty("value")]
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the phone number type
        /// </summary>
        [JsonProperty("type")]
        public string? Type { get; set; }

        /// <summary>
        /// Gets or sets whether this is the primary phone number
        /// </summary>
        [JsonProperty("primary")]
        public bool Primary { get; set; }

        /// <summary>
        /// Gets or sets the display value
        /// </summary>
        [JsonProperty("display")]
        public string? Display { get; set; }
    }

    /// <summary>
    /// SCIM instant messaging complex type
    /// </summary>
    public class ScimIms
    {
        /// <summary>
        /// Gets or sets the IM value
        /// </summary>
        [JsonProperty("value")]
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the IM type
        /// </summary>
        [JsonProperty("type")]
        public string? Type { get; set; }

        /// <summary>
        /// Gets or sets whether this is the primary IM
        /// </summary>
        [JsonProperty("primary")]
        public bool Primary { get; set; }

        /// <summary>
        /// Gets or sets the display value
        /// </summary>
        [JsonProperty("display")]
        public string? Display { get; set; }
    }

    /// <summary>
    /// SCIM photo complex type
    /// </summary>
    public class ScimPhoto
    {
        /// <summary>
        /// Gets or sets the photo URL
        /// </summary>
        [JsonProperty("value")]
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the photo type
        /// </summary>
        [JsonProperty("type")]
        public string? Type { get; set; }

        /// <summary>
        /// Gets or sets whether this is the primary photo
        /// </summary>
        [JsonProperty("primary")]
        public bool Primary { get; set; }

        /// <summary>
        /// Gets or sets the display value
        /// </summary>
        [JsonProperty("display")]
        public string? Display { get; set; }
    }

    /// <summary>
    /// SCIM address complex type
    /// </summary>
    public class ScimAddress
    {
        /// <summary>
        /// Gets or sets the formatted address
        /// </summary>
        [JsonProperty("formatted")]
        public string? Formatted { get; set; }

        /// <summary>
        /// Gets or sets the street address
        /// </summary>
        [JsonProperty("streetAddress")]
        public string? StreetAddress { get; set; }

        /// <summary>
        /// Gets or sets the locality
        /// </summary>
        [JsonProperty("locality")]
        public string? Locality { get; set; }

        /// <summary>
        /// Gets or sets the region
        /// </summary>
        [JsonProperty("region")]
        public string? Region { get; set; }

        /// <summary>
        /// Gets or sets the postal code
        /// </summary>
        [JsonProperty("postalCode")]
        public string? PostalCode { get; set; }

        /// <summary>
        /// Gets or sets the country
        /// </summary>
        [JsonProperty("country")]
        public string? Country { get; set; }

        /// <summary>
        /// Gets or sets the address type
        /// </summary>
        [JsonProperty("type")]
        public string? Type { get; set; }

        /// <summary>
        /// Gets or sets whether this is the primary address
        /// </summary>
        [JsonProperty("primary")]
        public bool Primary { get; set; }
    }

    /// <summary>
    /// SCIM group reference complex type
    /// </summary>
    public class ScimGroupRef
    {
        /// <summary>
        /// Gets or sets the group value (ID)
        /// </summary>
        [JsonProperty("value")]
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the reference URI
        /// </summary>
        [JsonProperty("$ref")]
        public string? Ref { get; set; }

        /// <summary>
        /// Gets or sets the display value
        /// </summary>
        [JsonProperty("display")]
        public string? Display { get; set; }

        /// <summary>
        /// Gets or sets the type
        /// </summary>
        [JsonProperty("type")]
        public string? Type { get; set; }
    }

    /// <summary>
    /// SCIM entitlement complex type
    /// </summary>
    public class ScimEntitlement
    {
        /// <summary>
        /// Gets or sets the entitlement value
        /// </summary>
        [JsonProperty("value")]
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the entitlement type
        /// </summary>
        [JsonProperty("type")]
        public string? Type { get; set; }

        /// <summary>
        /// Gets or sets whether this is the primary entitlement
        /// </summary>
        [JsonProperty("primary")]
        public bool Primary { get; set; }

        /// <summary>
        /// Gets or sets the display value
        /// </summary>
        [JsonProperty("display")]
        public string? Display { get; set; }
    }

    /// <summary>
    /// SCIM role complex type
    /// </summary>
    public class ScimRole
    {
        /// <summary>
        /// Gets or sets the role value
        /// </summary>
        [JsonProperty("value")]
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the role type
        /// </summary>
        [JsonProperty("type")]
        public string? Type { get; set; }

        /// <summary>
        /// Gets or sets whether this is the primary role
        /// </summary>
        [JsonProperty("primary")]
        public bool Primary { get; set; }

        /// <summary>
        /// Gets or sets the display value
        /// </summary>
        [JsonProperty("display")]
        public string? Display { get; set; }
    }

    /// <summary>
    /// SCIM X.509 certificate complex type
    /// </summary>
    public class ScimCertificate
    {
        /// <summary>
        /// Gets or sets the certificate value
        /// </summary>
        [JsonProperty("value")]
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the certificate type
        /// </summary>
        [JsonProperty("type")]
        public string? Type { get; set; }

        /// <summary>
        /// Gets or sets whether this is the primary certificate
        /// </summary>
        [JsonProperty("primary")]
        public bool Primary { get; set; }

        /// <summary>
        /// Gets or sets the display value
        /// </summary>
        [JsonProperty("display")]
        public string? Display { get; set; }
    }

    /// <summary>
    /// SCIM enterprise user extension
    /// </summary>
    public class ScimEnterpriseUser
    {
        /// <summary>
        /// Gets or sets the employee number
        /// </summary>
        [JsonProperty("employeeNumber")]
        public string? EmployeeNumber { get; set; }

        /// <summary>
        /// Gets or sets the cost center
        /// </summary>
        [JsonProperty("costCenter")]
        public string? CostCenter { get; set; }

        /// <summary>
        /// Gets or sets the organization
        /// </summary>
        [JsonProperty("organization")]
        public string? Organization { get; set; }

        /// <summary>
        /// Gets or sets the division
        /// </summary>
        [JsonProperty("division")]
        public string? Division { get; set; }

        /// <summary>
        /// Gets or sets the department
        /// </summary>
        [JsonProperty("department")]
        public string? Department { get; set; }

        /// <summary>
        /// Gets or sets the manager
        /// </summary>
        [JsonProperty("manager")]
        public ScimManager? Manager { get; set; }
    }

    /// <summary>
    /// SCIM manager reference
    /// </summary>
    public class ScimManager
    {
        /// <summary>
        /// Gets or sets the manager ID
        /// </summary>
        [JsonProperty("value")]
        public string? Value { get; set; }

        /// <summary>
        /// Gets or sets the reference URI
        /// </summary>
        [JsonProperty("$ref")]
        public string? Ref { get; set; }

        /// <summary>
        /// Gets or sets the display name
        /// </summary>
        [JsonProperty("displayName")]
        public string? DisplayName { get; set; }
    }
}