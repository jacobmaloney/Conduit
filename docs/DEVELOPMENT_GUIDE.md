# Conduit Development Guide

## Development Environment Setup

### Prerequisites

1. **Development Tools**
   - Visual Studio 2022 (recommended) or VS Code
   - .NET 8.0 SDK or later
   - SQL Server Developer Edition or LocalDB
   - Git for version control
   - Postman or similar API testing tool

2. **Optional Tools**
   - Docker Desktop for containerized development
   - Azure Data Studio for database management
   - Fiddler or similar HTTP debugging proxy

### Initial Setup

1. **Clone the Repository**
   ```bash
   git clone https://github.com/yourusername/Conduit.git
   cd Conduit
   ```

2. **Restore Dependencies**
   ```bash
   dotnet restore
   ```

3. **Database Setup**
   
   Option A: Using LocalDB (Recommended for development)
   ```bash
   # The application will auto-create the database on first run
   # Connection string in appsettings.Development.json:
   "Server=(localdb)\\mssqllocaldb;Database=Conduit_Dev;Trusted_Connection=True;"
   ```
   
   Option B: Using SQL Server
   ```sql
   -- Run the database creation script
   sqlcmd -S localhost -i Database/CreateDatabase.sql
   ```

4. **Configure Development Settings**
   
   Create `appsettings.Development.json`:
   ```json
   {
     "Logging": {
       "LogLevel": {
         "Default": "Debug",
         "Microsoft.AspNetCore": "Warning"
       }
     },
     "ConnectionStrings": {
       "DefaultConnection": "Server=(localdb)\\mssqllocaldb;Database=Conduit_Dev;Trusted_Connection=True;"
     },
     "JwtConfig": {
       "SecretKey": "YOUR_DEV_SECRET_KEY_AT_LEAST_32_CHARACTERS_LONG",
       "Issuer": "Conduit_Dev",
       "Audience": "ConduitAPI_Dev",
       "ExpirationMinutes": 60
     }
   }
   ```

5. **Run the Application**
   ```bash
   cd src/Conduit.Web
   dotnet run
   ```
   
   Navigate to: https://localhost:5001

## Project Structure

```
Conduit/
├── src/
│   ├── Conduit.Core/           # Business logic and models
│   │   ├── Models/                # SCIM resource models
│   │   ├── Services/              # Business services
│   │   └── Generation/            # Data generation utilities
│   │
│   ├── Conduit.DataAccess/     # Data access layer
│   │   ├── Repositories/          # Repository implementations
│   │   ├── DatabaseInitializer.cs # Database schema creation
│   │   └── DatabaseMigrator.cs    # Migration management
│   │
│   ├── Conduit.Web/            # Web application
│   │   ├── Controllers/           # API controllers
│   │   ├── Pages/                 # Blazor pages
│   │   ├── Services/              # Application services
│   │   ├── Authentication/        # JWT authentication
│   │   └── Middleware/            # Custom middleware
│   │
│   └── Conduit.Installer/      # Installation wizard
│       └── Steps/                 # Installation steps
│
├── Database/                       # SQL scripts
├── docs/                          # Documentation
└── tests/                         # Test projects (to be added)
```

## Development Workflow

### 1. Creating a New Feature

```bash
# Create feature branch
git checkout -b feature/your-feature-name

# Make changes
# ... edit files ...

# Run tests
dotnet test

# Commit changes
git add .
git commit -m "feat: Add your feature description"

# Push to remote
git push origin feature/your-feature-name
```

### 2. Code Style Guidelines

- **Naming Conventions**
  - PascalCase for public members
  - camelCase for private fields
  - _underscore prefix for private fields
  - Async methods end with Async

- **File Organization**
  - One class per file
  - File name matches class name
  - Related classes in same namespace

- **Documentation**
  - XML comments for public APIs
  - Inline comments for complex logic
  - README files for modules

### 3. Database Development

**Adding a New Table:**

1. Add migration script to `Database/` folder
2. Update `DatabaseInitializer.cs`
3. Create repository in `DataAccess/Repositories/`
4. Register repository in DI container

**Example Migration:**
```sql
-- Database/AddCustomTable.sql
CREATE TABLE CustomTable (
    Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    Name NVARCHAR(255) NOT NULL,
    Created DATETIME2 DEFAULT GETUTCDATE(),
    LastModified DATETIME2 DEFAULT GETUTCDATE()
);
```

### 4. API Development

**Adding a New Endpoint:**

1. Create controller in `Controllers/`
2. Inherit from `BaseScimController`
3. Add authentication attributes
4. Implement SCIM-compliant responses

**Example Controller:**
```csharp
[ApiController]
[Route("scim/v2/[controller]")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class CustomResourceController : BaseScimController
{
    private readonly ICustomRepository _repository;
    
    public CustomResourceController(ICustomRepository repository)
    {
        _repository = repository;
    }
    
    [HttpGet("{id}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var resource = await _repository.GetByIdAsync(id);
        if (resource == null)
            return ScimNotFound("Resource not found");
            
        return Ok(resource);
    }
}
```

## Testing

### Unit Testing

```csharp
// Example test using xUnit
public class UserRepositoryTests
{
    [Fact]
    public async Task GetByIdAsync_ReturnsUser_WhenUserExists()
    {
        // Arrange
        var repository = new UserRepository(connectionString);
        var userId = Guid.NewGuid();
        
        // Act
        var user = await repository.GetByIdAsync(userId);
        
        // Assert
        Assert.NotNull(user);
        Assert.Equal(userId, user.Id);
    }
}
```

### Integration Testing

```csharp
// Test API endpoints
public class UsersControllerIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    
    [Fact]
    public async Task Get_ReturnsSuccessAndCorrectContentType()
    {
        // Arrange
        var client = _factory.CreateClient();
        
        // Act
        var response = await client.GetAsync("/scim/v2/Users");
        
        // Assert
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/scim+json", 
            response.Content.Headers.ContentType.MediaType);
    }
}
```

### Manual Testing with Postman

1. Import the Postman collection from `docs/Conduit.postman_collection.json`
2. Set environment variables:
   - `base_url`: https://localhost:5001
   - `token`: Your JWT token
3. Run the test suite

## Debugging

### Visual Studio

1. Set `Conduit.Web` as startup project
2. Press F5 to start debugging
3. Set breakpoints as needed

### VS Code

1. Open the workspace
2. Go to Run and Debug (Ctrl+Shift+D)
3. Select ".NET Core Launch (web)"
4. Press F5

### Logging

Configure logging levels in `appsettings.Development.json`:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "Microsoft": "Information",
      "Microsoft.EntityFrameworkCore": "Warning",
      "Conduit": "Trace"
    }
  }
}
```

### Common Issues

**Issue: Database connection fails**
- Solution: Verify SQL Server is running
- Check connection string in appsettings
- Ensure database exists

**Issue: JWT token validation fails**
- Solution: Check secret key configuration
- Verify token hasn't expired
- Ensure clock sync between client/server

**Issue: CORS errors in browser**
- Solution: Configure CORS policy in Program.cs
- Add client origin to allowed origins
- Check preflight request handling

## Performance Profiling

### Using Application Insights

1. Add Application Insights package:
   ```bash
   dotnet add package Microsoft.ApplicationInsights.AspNetCore
   ```

2. Configure in `Program.cs`:
   ```csharp
   builder.Services.AddApplicationInsightsTelemetry();
   ```

3. View metrics in Azure Portal

### SQL Query Performance

1. Enable query logging:
   ```csharp
   optionsBuilder.LogTo(Console.WriteLine, LogLevel.Information)
       .EnableSensitiveDataLogging()
       .EnableDetailedErrors();
   ```

2. Use SQL Server Profiler for detailed analysis

## Deployment

### Local IIS

1. Publish the application:
   ```bash
   dotnet publish -c Release -o ./publish
   ```

2. Configure IIS site pointing to publish folder

### Docker

1. Build image:
   ```bash
   docker build -t conduit .
   ```

2. Run container:
   ```bash
   docker run -p 5000:5000 -e ConnectionStrings__DefaultConnection="..." conduit
   ```

### Azure App Service

1. Create App Service in Azure Portal
2. Configure connection strings
3. Deploy using GitHub Actions or Azure DevOps

## Contributing

### Pull Request Process

1. Fork the repository
2. Create feature branch
3. Make changes with tests
4. Update documentation
5. Submit pull request

### Code Review Checklist

- [ ] Code follows style guidelines
- [ ] Unit tests pass
- [ ] Documentation updated
- [ ] No security vulnerabilities
- [ ] Performance impact considered
- [ ] Backward compatibility maintained

## Resources

### SCIM Specification
- [RFC 7643](https://tools.ietf.org/html/rfc7643) - Core Schema
- [RFC 7644](https://tools.ietf.org/html/rfc7644) - Protocol

### .NET Documentation
- [ASP.NET Core](https://docs.microsoft.com/aspnet/core)
- [Blazor](https://docs.microsoft.com/aspnet/core/blazor)
- [Dapper](https://github.com/DapperLib/Dapper)

### Tools
- [Postman](https://www.postman.com/)
- [SQL Server Management Studio](https://docs.microsoft.com/sql/ssms)
- [Visual Studio](https://visualstudio.microsoft.com/)

## Support

For development questions:
- Check existing issues on GitHub
- Ask in discussions forum
- Contact the development team

## License

This project is licensed under the MIT License - see LICENSE file for details.