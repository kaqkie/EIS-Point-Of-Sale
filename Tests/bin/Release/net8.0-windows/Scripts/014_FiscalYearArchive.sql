/*
    Albert Retail Terminal — Phase 24 fiscal year archive & compression packages
    Run: sqlcmd -S .\SQLEXPRESS -E -i Scripts\014_FiscalYearArchive.sql
*/
SET NOCOUNT ON;
USE PointOfSale;
GO

IF OBJECT_ID(N'dbo.FiscalYearArchives', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FiscalYearArchives
    (
        ArchiveId                     BIGINT          IDENTITY(1,1) NOT NULL,
        FiscalYear                    INT             NOT NULL,
        PeriodStart                   DATE            NOT NULL,
        PeriodEnd                     DATE            NOT NULL,
        RolledOverAtUtc               DATETIME2(7)    NOT NULL CONSTRAINT DF_FiscalYearArchives_RolledOverAtUtc DEFAULT (SYSUTCDATETIME()),
        PrimarySupervisorUsername     NVARCHAR(80)    NOT NULL,
        SecondarySupervisorUsername   NVARCHAR(80)    NOT NULL,
        TotalGrossSalesMwk            DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_FiscalYearArchives_Gross DEFAULT (0),
        TotalVatCollectedMwk          DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_FiscalYearArchives_Vat DEFAULT (0),
        ExpectedClosureDays           INT             NOT NULL CONSTRAINT DF_FiscalYearArchives_ExpectedDays DEFAULT (0),
        ClosedDays                    INT             NOT NULL CONSTRAINT DF_FiscalYearArchives_ClosedDays DEFAULT (0),
        SyncedInvoiceCount            INT             NOT NULL CONSTRAINT DF_FiscalYearArchives_Synced DEFAULT (0),
        ManifestSha256                CHAR(64)        NOT NULL,
        ManifestHmacSha512            CHAR(128)       NOT NULL,
        ArchiveFilePath               NVARCHAR(500)   NOT NULL,
        ArchiveBytes                  BIGINT          NOT NULL CONSTRAINT DF_FiscalYearArchives_Bytes DEFAULT (0),
        CryptographicVerificationPassed BIT             NOT NULL CONSTRAINT DF_FiscalYearArchives_Crypto DEFAULT (0),
        Status                        VARCHAR(20)     NOT NULL CONSTRAINT DF_FiscalYearArchives_Status DEFAULT ('Locked'),
        Notes                         NVARCHAR(1000)  NULL,
        CONSTRAINT PK_FiscalYearArchives PRIMARY KEY CLUSTERED (ArchiveId)
    );

    CREATE UNIQUE INDEX UX_FiscalYearArchives_FiscalYear_Locked
        ON dbo.FiscalYearArchives (FiscalYear)
        WHERE Status = 'Locked';

    CREATE INDEX IX_FiscalYearArchives_RolledOver
        ON dbo.FiscalYearArchives (RolledOverAtUtc DESC, ArchiveId DESC);
END
GO

IF OBJECT_ID(N'dbo.FiscalArchivePackages', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FiscalArchivePackages
    (
        PackageId           BIGINT          IDENTITY(1,1) NOT NULL,
        CreatedAtUtc        DATETIME2(7)    NOT NULL CONSTRAINT DF_FiscalArchivePackages_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        PackageType         VARCHAR(40)     NOT NULL,
        PeriodStartUtc      DATETIME2(7)    NOT NULL,
        PeriodEndUtc        DATETIME2(7)    NOT NULL,
        FilePath            NVARCHAR(500)   NOT NULL,
        FileBytes           BIGINT          NOT NULL CONSTRAINT DF_FiscalArchivePackages_FileBytes DEFAULT (0),
        ContentSha256       CHAR(64)        NOT NULL,
        TriggeredByUsername NVARCHAR(80)    NOT NULL,
        DualKeyProtected    BIT             NOT NULL CONSTRAINT DF_FiscalArchivePackages_DualKey DEFAULT (1),
        CONSTRAINT PK_FiscalArchivePackages PRIMARY KEY CLUSTERED (PackageId)
    );

    CREATE INDEX IX_FiscalArchivePackages_Created
        ON dbo.FiscalArchivePackages (CreatedAtUtc DESC, PackageId DESC);
END
GO
