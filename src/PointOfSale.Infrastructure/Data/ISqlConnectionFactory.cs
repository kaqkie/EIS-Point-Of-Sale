using Microsoft.Data.SqlClient;

namespace PointOfSale.Infrastructure.Data;

public interface ISqlConnectionFactory
{
    Task<SqlConnection> CreateOpenConnectionAsync(CancellationToken cancellationToken = default);
}
