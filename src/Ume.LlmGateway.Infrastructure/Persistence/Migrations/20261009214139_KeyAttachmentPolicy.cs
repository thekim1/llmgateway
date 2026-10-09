using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ume.LlmGateway.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KeyAttachmentPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AttachmentPolicy",
                table: "VirtualKeys",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Allowed"); // existing keys keep today's behaviour
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttachmentPolicy",
                table: "VirtualKeys");
        }
    }
}
