using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Atelier.Workshops.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "workshops");

            migrationBuilder.CreateTable(
                name: "InboxMessages",
                schema: "workshops",
                columns: table => new
                {
                    Consumer = table.Column<string>(type: "text", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessages", x => new { x.Consumer, x.MessageId });
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                schema: "workshops",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Module = table.Column<string>(type: "text", nullable: false),
                    RoutingKey = table.Column<string>(type: "text", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Workshops",
                schema: "workshops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Track = table.Column<string>(type: "text", nullable: false),
                    Instructor = table.Column<string>(type: "text", nullable: false),
                    WorkshopDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PriceCents = table.Column<long>(type: "bigint", nullable: false),
                    SeatsTotal = table.Column<int>(type: "integer", nullable: false),
                    SeatsLeft = table.Column<int>(type: "integer", nullable: false),
                    KitsTotal = table.Column<int>(type: "integer", nullable: false),
                    KitsLeft = table.Column<int>(type: "integer", nullable: false),
                    KitPriceCents = table.Column<long>(type: "bigint", nullable: false),
                    MaxPerAttendee = table.Column<int>(type: "integer", nullable: false, defaultValue: 2),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workshops", x => x.Id);
                    table.CheckConstraint("CK_Workshops_Cap", "\"MaxPerAttendee\" >= 1");
                    table.CheckConstraint("CK_Workshops_Kits", "\"KitsLeft\" >= 0 AND \"KitsLeft\" <= \"KitsTotal\"");
                    table.CheckConstraint("CK_Workshops_Remaining", "\"SeatsLeft\" >= 0 AND \"SeatsLeft\" <= \"SeatsTotal\"");
                });

            migrationBuilder.CreateTable(
                name: "SeatHolds",
                schema: "workshops",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkshopId = table.Column<Guid>(type: "uuid", nullable: false),
                    Seats = table.Column<int>(type: "integer", nullable: false),
                    Kits = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleaseReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeatHolds", x => x.OrderId);
                    table.ForeignKey(
                        name: "FK_SeatHolds_Workshops_WorkshopId",
                        column: x => x.WorkshopId,
                        principalSchema: "workshops",
                        principalTable: "Workshops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkshopAttendees",
                schema: "workshops",
                columns: table => new
                {
                    WorkshopId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Seats = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkshopAttendees", x => new { x.WorkshopId, x.OwnerId });
                    table.CheckConstraint("CK_WorkshopAttendees_Seats", "\"Seats\" >= 0");
                    table.ForeignKey(
                        name: "FK_WorkshopAttendees_Workshops_WorkshopId",
                        column: x => x.WorkshopId,
                        principalSchema: "workshops",
                        principalTable: "Workshops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_SentAt",
                schema: "workshops",
                table: "OutboxMessages",
                column: "SentAt",
                filter: "\"SentAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SeatHolds_WorkshopId",
                schema: "workshops",
                table: "SeatHolds",
                column: "WorkshopId");

            migrationBuilder.CreateIndex(
                name: "IX_Workshops_Code",
                schema: "workshops",
                table: "Workshops",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboxMessages",
                schema: "workshops");

            migrationBuilder.DropTable(
                name: "OutboxMessages",
                schema: "workshops");

            migrationBuilder.DropTable(
                name: "SeatHolds",
                schema: "workshops");

            migrationBuilder.DropTable(
                name: "WorkshopAttendees",
                schema: "workshops");

            migrationBuilder.DropTable(
                name: "Workshops",
                schema: "workshops");
        }
    }
}
