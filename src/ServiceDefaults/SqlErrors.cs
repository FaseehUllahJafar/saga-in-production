using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ServiceDefaults;

public static class SqlErrors
{
    // 2627: PRIMARY KEY / UNIQUE constraint, 2601: unique index.
    public static bool IsUniqueViolation(this DbUpdateException e) =>
        e.InnerException is SqlException { Number: 2627 or 2601 };
}
