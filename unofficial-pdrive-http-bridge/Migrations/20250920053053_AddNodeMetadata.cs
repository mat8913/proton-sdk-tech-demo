using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace unofficial_pdrive_http_bridge.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TrackedVolumes",
                columns: table => new
                {
                    VolumeId = table.Column<string>(type: "TEXT", nullable: false),
                    LatestEventId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackedVolumes", x => x.VolumeId);
                });

            migrationBuilder.CreateTable(
                name: "TrackedFolders",
                columns: table => new
                {
                    VolumeId = table.Column<string>(type: "TEXT", nullable: false),
                    NodeId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackedFolders", x => new { x.VolumeId, x.NodeId });
                    table.ForeignKey(
                        name: "FK_TrackedFolders_TrackedVolumes_VolumeId",
                        column: x => x.VolumeId,
                        principalTable: "TrackedVolumes",
                        principalColumn: "VolumeId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NodeMetadata",
                columns: table => new
                {
                    VolumeId = table.Column<string>(type: "TEXT", nullable: false),
                    NodeId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ParentNodeId = table.Column<string>(type: "TEXT", nullable: false),
                    IsFile = table.Column<bool>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", nullable: true),
                    ActiveRevisionId = table.Column<string>(type: "TEXT", nullable: true),
                    Size = table.Column<long>(type: "INTEGER", nullable: true),
                    ModificationTime = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeMetadata", x => new { x.VolumeId, x.NodeId });
                    table.ForeignKey(
                        name: "FK_NodeMetadata_TrackedFolders_VolumeId_ParentNodeId",
                        columns: x => new { x.VolumeId, x.ParentNodeId },
                        principalTable: "TrackedFolders",
                        principalColumns: new[] { "VolumeId", "NodeId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NodeMetadata_VolumeId_ParentNodeId",
                table: "NodeMetadata",
                columns: new[] { "VolumeId", "ParentNodeId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NodeMetadata");

            migrationBuilder.DropTable(
                name: "TrackedFolders");

            migrationBuilder.DropTable(
                name: "TrackedVolumes");
        }
    }
}
