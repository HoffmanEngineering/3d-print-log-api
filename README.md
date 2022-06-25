dotnet ef migrations add <MigrationName> --project=PrintLogApi
dotnet ef database update

To get user ID in the controllers:
var userId = this.User.FindFirst(ClaimTypes.NameIdentifier).Value