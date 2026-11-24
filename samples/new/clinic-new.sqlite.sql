-- The rebuilt clinic database in SQLite's dialect (for the in-process tests).

CREATE TABLE payer (
    payer_id integer PRIMARY KEY,
    payer_code varchar(12) NOT NULL,
    name varchar(60) NOT NULL,
    copay_percent numeric(5,2) NOT NULL DEFAULT 0,
    phone varchar(20) NULL,
    UNIQUE (payer_code)
);
CREATE TABLE sex_codes (
    code char(1) NOT NULL,
    meaning varchar(20) NOT NULL,
    PRIMARY KEY (code)
);
CREATE TABLE claim_status (
    code varchar(10) NOT NULL,
    meaning varchar(40) NOT NULL,
    PRIMARY KEY (code)
);
CREATE TABLE patient (
    patient_id integer PRIMARY KEY,
    family_name varchar(40) NOT NULL,
    given_name varchar(30) NOT NULL,
    birth_date date NULL,
    sex_code char(1) NOT NULL,
    payer_id integer NULL,
    phone varchar(20) NULL,
    is_active boolean NOT NULL,
    created_by varchar(30) NOT NULL,
    FOREIGN KEY (sex_code) REFERENCES sex_codes (code),
    FOREIGN KEY (payer_id) REFERENCES payer (payer_id)
);
CREATE TABLE visit (
    visit_id integer PRIMARY KEY,
    patient_id integer NOT NULL,
    visit_at timestamp NOT NULL,
    clinician varchar(60) NULL,
    reason varchar(200) NULL,
    FOREIGN KEY (patient_id) REFERENCES patient (patient_id)
);
CREATE TABLE charge (
    charge_id integer PRIMARY KEY,
    patient_id integer NOT NULL,
    visit_id integer NULL,
    procedure_code char(5) NOT NULL,
    amount numeric(9,2) NOT NULL,
    copay numeric(9,2) NOT NULL DEFAULT 0,
    posted_at timestamp NOT NULL,
    CHECK (amount > 0),
    FOREIGN KEY (patient_id) REFERENCES patient (patient_id),
    FOREIGN KEY (visit_id) REFERENCES visit (visit_id)
);
CREATE TABLE claim (
    claim_id integer PRIMARY KEY,
    claim_number varchar(15) NOT NULL,
    patient_id integer NOT NULL,
    amount numeric(9,2) NOT NULL,
    status varchar(10) NOT NULL,
    entered_at timestamp NOT NULL,
    source_system varchar(20) NOT NULL,
    UNIQUE (claim_number),
    FOREIGN KEY (patient_id) REFERENCES patient (patient_id),
    FOREIGN KEY (status) REFERENCES claim_status (code)
);
