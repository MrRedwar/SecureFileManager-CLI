using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureFileManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ExternalDocumentId",
                table: "Operations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalPathSnapshot",
                table: "Operations",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExternalDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    PathKey = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalDocuments", x => x.Id);
                    table.CheckConstraint("CK_ExternalDocuments_Size", "[SizeBytes] >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Operations_ExternalDocumentId",
                table: "Operations",
                column: "ExternalDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalDocuments_PathKey",
                table: "ExternalDocuments",
                column: "PathKey",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Operations_ExternalDocuments_ExternalDocumentId",
                table: "Operations",
                column: "ExternalDocumentId",
                principalTable: "ExternalDocuments",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Operations_ExternalDocuments_ExternalDocumentId",
                table: "Operations");

            migrationBuilder.DropTable(
                name: "ExternalDocuments");

            migrationBuilder.DropIndex(
                name: "IX_Operations_ExternalDocumentId",
                table: "Operations");

            migrationBuilder.DropColumn(
                name: "ExternalDocumentId",
                table: "Operations");

            migrationBuilder.DropColumn(
                name: "ExternalPathSnapshot",
                table: "Operations");
        }
    }
}
