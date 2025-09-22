using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace unofficial_pdrive_http_bridge.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SecretsCacheGroups",
                columns: table => new
                {
                    Context_HasValue = table.Column<bool>(type: "INTEGER", nullable: false),
                    Context_Name = table.Column<string>(type: "TEXT", nullable: false),
                    Context_Id = table.Column<string>(type: "TEXT", nullable: false),
                    ValueHolderName = table.Column<string>(type: "TEXT", nullable: false),
                    ValueHolderId = table.Column<string>(type: "TEXT", nullable: false),
                    ValueName = table.Column<string>(type: "TEXT", nullable: false),
                    Secret_Context_HasValue = table.Column<bool>(type: "INTEGER", nullable: false),
                    Secret_Context_Name = table.Column<string>(type: "TEXT", nullable: false),
                    Secret_Context_Id = table.Column<string>(type: "TEXT", nullable: false),
                    Secret_ValueHolderName = table.Column<string>(type: "TEXT", nullable: false),
                    Secret_ValueHolderId = table.Column<string>(type: "TEXT", nullable: false),
                    Secret_ValueName = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecretsCacheGroups", x => new { x.Context_HasValue, x.Context_Name, x.Context_Id, x.ValueHolderName, x.ValueHolderId, x.ValueName, x.Secret_Context_HasValue, x.Secret_Context_Name, x.Secret_Context_Id, x.Secret_ValueHolderName, x.Secret_ValueHolderId, x.Secret_ValueName });
                });

            migrationBuilder.CreateTable(
                name: "SecretsCacheSecrets",
                columns: table => new
                {
                    Context_HasValue = table.Column<bool>(type: "INTEGER", nullable: false),
                    Context_Name = table.Column<string>(type: "TEXT", nullable: false),
                    Context_Id = table.Column<string>(type: "TEXT", nullable: false),
                    ValueHolderName = table.Column<string>(type: "TEXT", nullable: false),
                    ValueHolderId = table.Column<string>(type: "TEXT", nullable: false),
                    ValueName = table.Column<string>(type: "TEXT", nullable: false),
                    SecretBytes = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Flags = table.Column<byte>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecretsCacheSecrets", x => new { x.Context_HasValue, x.Context_Name, x.Context_Id, x.ValueHolderName, x.ValueHolderId, x.ValueName });
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    AccessToken = table.Column<string>(type: "TEXT", nullable: false),
                    RefreshToken = table.Column<string>(type: "TEXT", nullable: false),
                    IsWaitingForSecondFactorCode = table.Column<bool>(type: "INTEGER", nullable: false),
                    PasswordMode = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.SessionId);
                });

            migrationBuilder.CreateTable(
                name: "SessionScopes",
                columns: table => new
                {
                    Scope = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionScopes", x => x.Scope);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SecretsCacheGroups");

            migrationBuilder.DropTable(
                name: "SecretsCacheSecrets");

            migrationBuilder.DropTable(
                name: "Sessions");

            migrationBuilder.DropTable(
                name: "SessionScopes");
        }
    }
}
