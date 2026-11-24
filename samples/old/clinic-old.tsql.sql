-- The legacy clinic database, as deployed (SQL Server). Invented sample data model.

CREATE TABLE dbo.tblPayer (
    PayerCode varchar(12) NOT NULL,
    PayerName varchar(60) NOT NULL,
    CopayPct decimal(5,2) NULL,
    Phone varchar(20) NULL,
    CONSTRAINT PK_tblPayer PRIMARY KEY (PayerCode)
);
GO
CREATE TABLE dbo.tblPatient (
    PatID int IDENTITY(1,1) NOT NULL,
    PatName varchar(70) NOT NULL,
    DOB varchar(10) NULL,
    Sex char(1) NULL,
    PayerCode varchar(12) NULL,
    Active char(1) NOT NULL DEFAULT 'Y',
    Phone varchar(20) NULL,
    Fax varchar(20) NULL,
    Deleted bit NOT NULL DEFAULT 0,
    EnteredBy varchar(20) NULL,
    CONSTRAINT PK_tblPatient PRIMARY KEY (PatID),
    CONSTRAINT FK_tblPatient_tblPayer FOREIGN KEY (PayerCode) REFERENCES dbo.tblPayer (PayerCode)
);
GO
CREATE TABLE dbo.tblVisit (
    VisitID int IDENTITY(1,1) NOT NULL,
    PatID int NOT NULL,
    VisitDt datetime NOT NULL,
    Clinician varchar(60) NULL,
    Reason varchar(255) NULL,
    CONSTRAINT PK_tblVisit PRIMARY KEY (VisitID),
    CONSTRAINT FK_tblVisit_tblPatient FOREIGN KEY (PatID) REFERENCES dbo.tblPatient (PatID)
);
GO
CREATE TABLE dbo.tblCharge (
    ChgID int IDENTITY(1,1) NOT NULL,
    PatID int NOT NULL,
    VisitID int NULL,
    ProcCode varchar(10) NOT NULL,
    Amt money NOT NULL,
    Copay money NULL,
    PostedOn datetime NOT NULL,
    CONSTRAINT PK_tblCharge PRIMARY KEY (ChgID),
    CONSTRAINT FK_tblCharge_tblVisit FOREIGN KEY (VisitID) REFERENCES dbo.tblVisit (VisitID)
);
GO
CREATE TABLE dbo.tblClaim (
    ClaimNo varchar(15) NOT NULL,
    PatID int NOT NULL,
    Amt money NOT NULL,
    Status varchar(8) NOT NULL,
    EnteredOn datetime NOT NULL,
    CONSTRAINT PK_tblClaim PRIMARY KEY (ClaimNo),
    CONSTRAINT FK_tblClaim_tblPatient FOREIGN KEY (PatID) REFERENCES dbo.tblPatient (PatID)
);
GO
CREATE TABLE dbo.FaxQueue (
    FaxId int IDENTITY(1,1) NOT NULL,
    Number varchar(20) NOT NULL,
    Document varbinary(max) NULL,
    CONSTRAINT PK_FaxQueue PRIMARY KEY (FaxId)
);
GO
