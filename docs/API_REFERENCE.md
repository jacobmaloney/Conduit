# Conduit API Reference

## Overview

The Conduit implements the SCIM 2.0 (System for Cross-domain Identity Management) protocol, providing a standardized REST API for identity provisioning and management.

## Base Configuration

### Base URL
```
https://{your-server}/scim/v2
```

### Authentication
All API requests require authentication using JWT Bearer tokens.

```http
Authorization: Bearer {token}
```

### Content Types
- **Request**: `application/scim+json` or `application/json`
- **Response**: `application/scim+json`

### HTTP Status Codes
- `200 OK` - Successful GET or PATCH request
- `201 Created` - Successful POST request
- `204 No Content` - Successful DELETE request
- `400 Bad Request` - Invalid request format or parameters
- `401 Unauthorized` - Missing or invalid authentication
- `403 Forbidden` - Insufficient permissions
- `404 Not Found` - Resource not found
- `409 Conflict` - Resource conflict (e.g., duplicate userName)
- `500 Internal Server Error` - Server error

## User Resource

### User Schema
```json
{
  "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
  "id": "2819c223-7f76-453a-919d-413861904646",
  "externalId": "external-123",
  "userName": "john.doe@example.com",
  "name": {
    "formatted": "Mr. John M. Doe, Jr.",
    "familyName": "Doe",
    "givenName": "John",
    "middleName": "Michael",
    "honorificPrefix": "Mr.",
    "honorificSuffix": "Jr."
  },
  "displayName": "John Doe",
  "nickName": "Johnny",
  "profileUrl": "https://example.com/profile/johndoe",
  "emails": [
    {
      "value": "john.doe@example.com",
      "type": "work",
      "primary": true
    }
  ],
  "addresses": [
    {
      "type": "work",
      "streetAddress": "123 Main St",
      "locality": "Anytown",
      "region": "CA",
      "postalCode": "12345",
      "country": "USA",
      "formatted": "123 Main St\nAnytown, CA 12345\nUSA"
    }
  ],
  "phoneNumbers": [
    {
      "value": "+1-555-555-5555",
      "type": "work"
    }
  ],
  "ims": [
    {
      "value": "johndoe",
      "type": "skype"
    }
  ],
  "photos": [
    {
      "value": "https://example.com/photo.jpg",
      "type": "photo"
    }
  ],
  "userType": "Employee",
  "title": "Software Engineer",
  "preferredLanguage": "en-US",
  "locale": "en-US",
  "timezone": "America/Los_Angeles",
  "active": true,
  "password": "hidden",
  "meta": {
    "resourceType": "User",
    "created": "2024-01-01T00:00:00Z",
    "lastModified": "2024-01-01T00:00:00Z",
    "location": "/scim/v2/Users/2819c223-7f76-453a-919d-413861904646",
    "version": "W/\"1\""
  }
}
```

### User Endpoints

#### List Users
Retrieve a paginated list of users with optional filtering and sorting.

```http
GET /scim/v2/Users?startIndex=1&count=10&filter=userName eq "john.doe@example.com"
```

**Query Parameters:**
- `startIndex` (integer): Starting index for pagination (1-based)
- `count` (integer): Number of results per page (default: 10, max: 100)
- `filter` (string): SCIM filter expression
- `sortBy` (string): Attribute to sort by
- `sortOrder` (string): `ascending` or `descending`
- `attributes` (string): Comma-separated list of attributes to return
- `excludedAttributes` (string): Comma-separated list of attributes to exclude

**Response:**
```json
{
  "schemas": ["urn:ietf:params:scim:api:messages:2.0:ListResponse"],
  "totalResults": 100,
  "startIndex": 1,
  "itemsPerPage": 10,
  "Resources": [
    {
      // User objects
    }
  ]
}
```

#### Get User by ID
Retrieve a specific user by their unique identifier.

```http
GET /scim/v2/Users/{id}
```

**Path Parameters:**
- `id` (string): User's unique identifier (GUID)

**Response:** User object

#### Create User
Create a new user resource.

```http
POST /scim/v2/Users
Content-Type: application/scim+json

{
  "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
  "userName": "jane.smith@example.com",
  "name": {
    "givenName": "Jane",
    "familyName": "Smith"
  },
  "emails": [
    {
      "value": "jane.smith@example.com",
      "type": "work",
      "primary": true
    }
  ],
  "active": true
}
```

**Response:** Created user object with `201 Created` status

#### Update User (Replace)
Replace an entire user resource.

```http
PUT /scim/v2/Users/{id}
Content-Type: application/scim+json

{
  "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
  "id": "{id}",
  "userName": "jane.smith@example.com",
  // Complete user object
}
```

**Response:** Updated user object

#### Update User (Patch)
Partially update a user resource using SCIM PATCH operations.

```http
PATCH /scim/v2/Users/{id}
Content-Type: application/scim+json

{
  "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
  "Operations": [
    {
      "op": "replace",
      "path": "name.givenName",
      "value": "Janet"
    },
    {
      "op": "add",
      "path": "emails",
      "value": [
        {
          "value": "janet.smith@personal.com",
          "type": "home"
        }
      ]
    },
    {
      "op": "remove",
      "path": "phoneNumbers[type eq \"home\"]"
    }
  ]
}
```

**Supported Operations:**
- `add`: Add new values to an attribute
- `replace`: Replace existing values
- `remove`: Remove values from an attribute

**Response:** Updated user object

#### Delete User
Delete a user resource.

```http
DELETE /scim/v2/Users/{id}
```

**Response:** `204 No Content` on success

## Enterprise User Extension

### Enterprise Schema
The enterprise extension adds organizational attributes to users.

```json
{
  "schemas": [
    "urn:ietf:params:scim:schemas:core:2.0:User",
    "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User"
  ],
  "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User": {
    "employeeNumber": "12345",
    "costCenter": "CC-100",
    "organization": "ACME Corp",
    "division": "Engineering",
    "department": "Software Development",
    "manager": {
      "value": "manager-id-123",
      "$ref": "/scim/v2/Users/manager-id-123",
      "displayName": "Alice Manager"
    }
  }
}
```

## Group Resource (Coming Soon)

### Group Schema
```json
{
  "schemas": ["urn:ietf:params:scim:schemas:core:2.0:Group"],
  "id": "group-id-123",
  "displayName": "Engineering Team",
  "members": [
    {
      "value": "user-id-123",
      "$ref": "/scim/v2/Users/user-id-123",
      "type": "User",
      "display": "John Doe"
    }
  ],
  "meta": {
    "resourceType": "Group",
    "created": "2024-01-01T00:00:00Z",
    "lastModified": "2024-01-01T00:00:00Z",
    "location": "/scim/v2/Groups/group-id-123",
    "version": "W/\"1\""
  }
}
```

## Error Responses

### SCIM Error Format
All errors follow the SCIM error schema:

```json
{
  "schemas": ["urn:ietf:params:scim:api:messages:2.0:Error"],
  "status": "400",
  "scimType": "invalidSyntax",
  "detail": "The 'userName' attribute is required"
}
```

### Common SCIM Error Types
- `invalidFilter` - Invalid filter expression
- `tooMany` - Too many operations or results
- `uniqueness` - Unique constraint violation
- `mutability` - Attempt to modify immutable attribute
- `invalidSyntax` - Invalid request syntax
- `invalidPath` - Invalid attribute path
- `noTarget` - Target resource not found
- `invalidValue` - Invalid attribute value
- `invalidVers` - Version mismatch for conditional update
- `sensitive` - Sensitive information in error

## Filtering

### Filter Syntax
SCIM supports complex filtering using a subset of the RFC 7644 specification.

**Operators:**
- `eq` - Equal
- `ne` - Not equal
- `co` - Contains
- `sw` - Starts with
- `ew` - Ends with
- `pr` - Present (has value)
- `gt` - Greater than
- `ge` - Greater than or equal
- `lt` - Less than
- `le` - Less than or equal

**Logical Operators:**
- `and` - Logical AND
- `or` - Logical OR
- `not` - Logical NOT

**Examples:**
```
# Simple equality
userName eq "john.doe@example.com"

# Complex filter
(userName sw "john" or emails.value co "@example.com") and active eq true

# Nested attributes
name.givenName eq "John" and name.familyName eq "Doe"

# Multi-valued attributes
emails[type eq "work" and primary eq true].value co "@company.com"
```

## Pagination

### Request Parameters
- `startIndex`: 1-based index of the first result (default: 1)
- `count`: Number of results per page (default: 10)

### Response Metadata
```json
{
  "totalResults": 500,
  "startIndex": 11,
  "itemsPerPage": 10
}
```

## Sorting

### Request Parameter
```
sortBy=userName&sortOrder=ascending
```

### Supported Sort Orders
- `ascending` (default)
- `descending`

## Attribute Selection

### Including Specific Attributes
```
GET /scim/v2/Users?attributes=userName,name,emails
```

### Excluding Attributes
```
GET /scim/v2/Users?excludedAttributes=photos,ims
```

## Authentication API

### Generate JWT Token
```http
POST /api/auth/token
Content-Type: application/json

{
  "username": "admin",
  "password": "password"
}
```

**Response:**
```json
{
  "token": "eyJhbGciOiJIUzI1NiIs...",
  "expiresIn": 3600
}
```

### Refresh Token
```http
POST /api/auth/refresh
Authorization: Bearer {current-token}
```

## API Tokens

### Create API Token
```http
POST /api/tokens
Content-Type: application/json

{
  "name": "Integration Token",
  "expiresAt": "2025-01-01T00:00:00Z"
}
```

**Response:**
```json
{
  "token": "scim_1234567890abcdef",
  "name": "Integration Token",
  "createdAt": "2024-01-01T00:00:00Z",
  "expiresAt": "2025-01-01T00:00:00Z"
}
```

### List API Tokens
```http
GET /api/tokens
```

### Revoke API Token
```http
DELETE /api/tokens/{id}
```

## Rate Limiting (Planned)

Future versions will implement rate limiting with the following headers:
- `X-RateLimit-Limit`: Maximum requests per window
- `X-RateLimit-Remaining`: Remaining requests in current window
- `X-RateLimit-Reset`: Unix timestamp when window resets

## Webhooks (Planned)

Future versions will support webhooks for real-time notifications of resource changes.

## Best Practices

### Performance
1. Use pagination for large result sets
2. Specify only required attributes using `attributes` parameter
3. Use specific filters to reduce result set size
4. Implement caching on the client side

### Security
1. Always use HTTPS in production
2. Rotate API tokens regularly
3. Implement least-privilege access
4. Monitor API usage through audit logs
5. Validate and sanitize all input data

### Error Handling
1. Check HTTP status codes first
2. Parse SCIM error responses for details
3. Implement exponential backoff for rate limits
4. Log all errors for debugging

### Compatibility
1. Always include the `schemas` attribute
2. Handle unknown attributes gracefully
3. Support both `application/json` and `application/scim+json`
4. Implement proper version negotiation

## Testing

### Test Endpoints
```bash
# Get server configuration (planned)
curl -X GET https://localhost:5001/scim/v2/ServiceProviderConfig

# Test authentication
curl -X GET https://localhost:5001/scim/v2/Users \
  -H "Authorization: Bearer {token}"

# Create test user
curl -X POST https://localhost:5001/scim/v2/Users \
  -H "Authorization: Bearer {token}" \
  -H "Content-Type: application/scim+json" \
  -d '{
    "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
    "userName": "test@example.com",
    "active": true
  }'
```

## Additional Resources

- [SCIM 2.0 RFC 7643](https://tools.ietf.org/html/rfc7643) - Core Schema
- [SCIM 2.0 RFC 7644](https://tools.ietf.org/html/rfc7644) - Protocol
- [SCIM.cloud](https://scim.cloud) - SCIM Resources