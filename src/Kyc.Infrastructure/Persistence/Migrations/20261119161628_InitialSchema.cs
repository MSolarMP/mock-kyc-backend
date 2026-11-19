using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Kyc.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    actor_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    actor_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "handoff_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issued_by_token_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    redeemed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    redeemed_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    redeemed_user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_handoff_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "partners",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    image_retention_days = table.Column<int>(type: "integer", nullable: false),
                    webhook_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_partners", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "session_access_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    via_handoff = table.Column<bool>(type: "boolean", nullable: false),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    device_fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_access_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workflow_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    run_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    locked_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "partner_api_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    partner_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    key_prefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_partner_api_keys", x => x.id);
                    table.ForeignKey(
                        name: "fk_partner_api_keys_partners_partner_id",
                        column: x => x.partner_id,
                        principalTable: "partners",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "verification_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    applicant_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    partner_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    document_attempts = table.Column<int>(type: "integer", nullable: false),
                    selfie_attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    idempotency_request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    device_fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    document_number_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    extracted_identity_encrypted = table.Column<byte[]>(type: "bytea", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_verification_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_verification_sessions_partners_partner_id",
                        column: x => x.partner_id,
                        principalTable: "partners",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    content_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    declared_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    declared_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    integrity_verified = table.Column<bool>(type: "boolean", nullable: false),
                    uploaded_by_token_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    purged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_artifacts", x => x.id);
                    table.ForeignKey(
                        name: "fk_artifacts_verification_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "verification_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "decisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    decided_by = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reviewer_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    reason_codes = table.Column<List<string>>(type: "text[]", nullable: false),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    rules_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    risk_score = table.Column<int>(type: "integer", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_decisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_decisions_verification_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "verification_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "provider_checks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    provider_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    score = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    reason_codes = table.Column<List<string>>(type: "text[]", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    duration_ms = table.Column<long>(type: "bigint", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_checks", x => x.id);
                    table.ForeignKey(
                        name: "fk_provider_checks_verification_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "verification_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_artifacts_declared_sha256",
                table: "artifacts",
                column: "declared_sha256");

            migrationBuilder.CreateIndex(
                name: "ix_artifacts_session_id",
                table: "artifacts",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_artifacts_status_uploaded_at",
                table: "artifacts",
                columns: new[] { "status", "uploaded_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_event_type_occurred_at",
                table: "audit_events",
                columns: new[] { "event_type", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_session_id_id",
                table: "audit_events",
                columns: new[] { "session_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_decisions_session_id_decided_at",
                table: "decisions",
                columns: new[] { "session_id", "decided_at" });

            migrationBuilder.CreateIndex(
                name: "ix_handoff_tokens_session_id",
                table: "handoff_tokens",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_handoff_tokens_token_hash",
                table: "handoff_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_partner_api_keys_key_hash",
                table: "partner_api_keys",
                column: "key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_partner_api_keys_partner_id",
                table: "partner_api_keys",
                column: "partner_id");

            migrationBuilder.CreateIndex(
                name: "ix_provider_checks_artifact_id_type",
                table: "provider_checks",
                columns: new[] { "artifact_id", "type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_provider_checks_session_id_type",
                table: "provider_checks",
                columns: new[] { "session_id", "type" });

            migrationBuilder.CreateIndex(
                name: "ix_session_access_tokens_session_id",
                table: "session_access_tokens",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_session_access_tokens_token_hash",
                table: "session_access_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_verification_sessions_created_ip_created_at",
                table: "verification_sessions",
                columns: new[] { "created_ip", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_verification_sessions_document_number_hash",
                table: "verification_sessions",
                column: "document_number_hash",
                filter: "document_number_hash IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_verification_sessions_partner_id_applicant_reference",
                table: "verification_sessions",
                columns: new[] { "partner_id", "applicant_reference" });

            migrationBuilder.CreateIndex(
                name: "ix_verification_sessions_partner_id_idempotency_key",
                table: "verification_sessions",
                columns: new[] { "partner_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_verification_sessions_status_updated_at",
                table: "verification_sessions",
                columns: new[] { "status", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_jobs_runnable",
                table: "workflow_jobs",
                column: "run_after",
                filter: "status IN ('Pending','Running')");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_jobs_session_id",
                table: "workflow_jobs",
                column: "session_id");

            // Audit trail is append-only, enforced by the database rather than by convention, so it holds even for
            // code paths (or people) that bypass the application. In production the app role would also have
            // only INSERT/SELECT on this table, and the table is shipped to WORM storage / a SIEM.
            // NOTE: hand-written. Regenerating this migration drops it; re-add it.
            migrationBuilder.Sql("""
                CREATE FUNCTION audit_events_block_mutation() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'audit_events is append-only (% blocked)', TG_OP;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER audit_events_append_only
                    BEFORE UPDATE OR DELETE ON audit_events
                    FOR EACH ROW EXECUTE FUNCTION audit_events_block_mutation();

                CREATE TRIGGER audit_events_no_truncate
                    BEFORE TRUNCATE ON audit_events
                    FOR EACH STATEMENT EXECUTE FUNCTION audit_events_block_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS audit_events_no_truncate ON audit_events;
                DROP TRIGGER IF EXISTS audit_events_append_only ON audit_events;
                DROP FUNCTION IF EXISTS audit_events_block_mutation();
                """);
            migrationBuilder.DropTable(
                name: "artifacts");

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "decisions");

            migrationBuilder.DropTable(
                name: "handoff_tokens");

            migrationBuilder.DropTable(
                name: "partner_api_keys");

            migrationBuilder.DropTable(
                name: "provider_checks");

            migrationBuilder.DropTable(
                name: "session_access_tokens");

            migrationBuilder.DropTable(
                name: "workflow_jobs");

            migrationBuilder.DropTable(
                name: "verification_sessions");

            migrationBuilder.DropTable(
                name: "partners");
        }
    }
}
