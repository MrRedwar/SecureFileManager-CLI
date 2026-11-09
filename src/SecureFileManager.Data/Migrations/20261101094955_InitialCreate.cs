using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureFileManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, collation: "Latin1_General_100_CI_AS"),
                    PasswordHash = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    FailedLoginCount = table.Column<int>(type: "int", nullable: false),
                    LockoutUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.CheckConstraint("CK_Users_FailedLoginCount", "[FailedLoginCount] >= 0");
                    table.CheckConstraint("CK_Users_PasswordHash", "LEN([PasswordHash]) > 0");
                    table.CheckConstraint("CK_Users_Username", "LEN(LTRIM(RTRIM([Username]))) BETWEEN 3 AND 50 AND DATALENGTH([Username]) = DATALENGTH(LTRIM(RTRIM([Username])))");
                });

            migrationBuilder.CreateTable(
                name: "Files",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    RelativePath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false, collation: "Latin1_General_100_CI_AS"),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Extension = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Files", x => x.Id);
                    table.CheckConstraint("CK_Files_Dates", "[ModifiedAtUtc] >= [CreatedAtUtc] AND ([DeletedAtUtc] IS NULL OR [DeletedAtUtc] >= [CreatedAtUtc])");
                    table.CheckConstraint("CK_Files_Kind", "[Kind] IN ('File', 'Directory')");
                    table.CheckConstraint("CK_Files_Name", "LEN([Name]) > 0");
                    table.CheckConstraint("CK_Files_RelativePath", "LEN([RelativePath]) > 0");
                    table.CheckConstraint("CK_Files_SizeBytes", "[SizeBytes] >= 0");
                    table.CheckConstraint("CK_Files_SoftDelete", "([IsDeleted] = 0 AND [DeletedAtUtc] IS NULL) OR ([IsDeleted] = 1 AND [DeletedAtUtc] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_Files_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Operations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorNameSnapshot = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    FileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    OperationType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Result = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    TargetPathSnapshot = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Details = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Operations", x => x.Id);
                    table.CheckConstraint("CK_Operations_Result", "[Result] IN ('Success', 'Denied', 'Failed')");
                    table.CheckConstraint("CK_Operations_Type", "[OperationType] IN ('LOGIN_SUCCESS', 'LOGIN_FAILED', 'LOGOUT', 'FILE_CREATED', 'FILE_READ', 'FILE_UPDATED', 'FILE_DELETED', 'DIRECTORY_CREATED', 'JSON_VALIDATED', 'XML_VALIDATED', 'ZIP_CREATED', 'ZIP_EXTRACTED', 'ACCESS_DENIED', 'OPERATION_FAILED')");
                    table.ForeignKey(
                        name: "FK_Operations_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Operations_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Files_OwnerId",
                table: "Files",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Files_OwnerId_RelativePath",
                table: "Files",
                columns: new[] { "OwnerId", "RelativePath" },
                unique: true,
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Operations_CorrelationId",
                table: "Operations",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_Operations_FileId",
                table: "Operations",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_Operations_OperationType_Result",
                table: "Operations",
                columns: new[] { "OperationType", "Result" });

            migrationBuilder.CreateIndex(
                name: "IX_Operations_UserId_OccurredAtUtc",
                table: "Operations",
                columns: new[] { "UserId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Operations");

            migrationBuilder.DropTable(
                name: "Files");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
