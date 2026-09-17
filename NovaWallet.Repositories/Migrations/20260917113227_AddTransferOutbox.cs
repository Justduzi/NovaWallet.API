using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NovaWallet.Repositories.Migrations
{
    /// <inheritdoc />
    public partial class AddTransferOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TransferReference = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                    table.CheckConstraint("CK_Outbox_SchemaVersion", "[SchemaVersion] > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_CreatedAtUtc_Id",
                table: "OutboxMessages",
                columns: new[] { "CreatedAtUtc", "Id" },
                filter: "[PublishedAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_TransferReference",
                table: "OutboxMessages",
                column: "TransferReference",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutboxMessages");
        }
    }
}
