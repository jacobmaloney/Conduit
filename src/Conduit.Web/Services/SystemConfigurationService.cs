using System.Threading.Tasks;
using Conduit.DataAccess.Repositories;

namespace Conduit.Web.Services
{
    /// <summary>
    /// Thin accessor over the SystemConfiguration key/value table.
    /// Used to surface runtime configuration (e.g. SqlEmulator.ConnectionString,
    /// ArsProxy.Server / .Username / .Password) without baking them into appsettings.
    /// </summary>
    public class SystemConfigurationService
    {
        private readonly SystemConfigurationRepository _repository;

        public SystemConfigurationService(SystemConfigurationRepository repository)
        {
            _repository = repository;
        }

        public Task<string?> GetAsync(string key) => _repository.GetValueAsync(key);

        public Task SetAsync(string key, string value, string type = "String", string? description = null) =>
            _repository.UpsertAsync(key, value, type, description);
    }
}
