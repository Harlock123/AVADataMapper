-- The rebuilt clinic database (PostgreSQL). Invented sample data model.

CREATE TABLE payer (
    payer_id integer GENERATED ALWAYS AS IDENTITY NOT NULL,
    payer_code varchar(12) NOT NULL,
    name varchar(60) NOT NULL,
    copay_percent numeric(5,2) NOT NULL DEFAULT 0,
    phone varchar(20) NULL,
    CONSTRAINT payer_pkey PRIMARY KEY (payer_id),
    CONSTRAINT payer_payer_code_key UNIQUE (payer_code)
);
CREATE TABLE sex_codes (
    code char(1) NOT NULL,
    meaning varchar(20) NOT NULL,
    CONSTRAINT sex_codes_pkey PRIMARY KEY (code)
);
CREATE TABLE claim_status (
    code varchar(10) NOT NULL,
    meaning varchar(40) NOT NULL,
    CONSTRAINT claim_status_pkey PRIMARY KEY (code)
);
CREATE TABLE patient (
    patient_id integer GENERATED ALWAYS AS IDENTITY NOT NULL,
    family_name varchar(40) NOT NULL,
    given_name varchar(30) NOT NULL,
    birth_date date NULL,
    sex_code char(1) NOT NULL,
    payer_id integer NULL,
    phone varchar(20) NULL,
    is_active boolean NOT NULL,
    created_by varchar(30) NOT NULL,
    CONSTRAINT patient_pkey PRIMARY KEY (patient_id),
    CONSTRAINT patient_sex_code_fkey FOREIGN KEY (sex_code) REFERENCES sex_codes (code),
    CONSTRAINT patient_payer_id_fkey FOREIGN KEY (payer_id) REFERENCES payer (payer_id)
);
CREATE TABLE visit (
    visit_id integer GENERATED ALWAYS AS IDENTITY NOT NULL,
    patient_id integer NOT NULL,
    visit_at timestamp NOT NULL,
    clinician varchar(60) NULL,
    reason varchar(200) NULL,
    CONSTRAINT visit_pkey PRIMARY KEY (visit_id),
    CONSTRAINT visit_patient_id_fkey FOREIGN KEY (patient_id) REFERENCES patient (patient_id)
);
CREATE TABLE charge (
    charge_id integer GENERATED ALWAYS AS IDENTITY NOT NULL,
    patient_id integer NOT NULL,
    visit_id integer NULL,
    procedure_code char(5) NOT NULL,
    amount numeric(9,2) NOT NULL,
    copay numeric(9,2) NOT NULL DEFAULT 0,
    posted_at timestamp NOT NULL,
    CONSTRAINT charge_pkey PRIMARY KEY (charge_id),
    CONSTRAINT charge_check1 CHECK (amount > 0),
    CONSTRAINT charge_patient_id_fkey FOREIGN KEY (patient_id) REFERENCES patient (patient_id),
    CONSTRAINT charge_visit_id_fkey FOREIGN KEY (visit_id) REFERENCES visit (visit_id)
);
CREATE TABLE claim (
    claim_id integer GENERATED ALWAYS AS IDENTITY NOT NULL,
    claim_number varchar(15) NOT NULL,
    patient_id integer NOT NULL,
    amount numeric(9,2) NOT NULL,
    status varchar(10) NOT NULL,
    entered_at timestamp NOT NULL,
    source_system varchar(20) NOT NULL,
    CONSTRAINT claim_pkey PRIMARY KEY (claim_id),
    CONSTRAINT claim_claim_number_key UNIQUE (claim_number),
    CONSTRAINT claim_patient_id_fkey FOREIGN KEY (patient_id) REFERENCES patient (patient_id),
    CONSTRAINT claim_status_fkey FOREIGN KEY (status) REFERENCES claim_status (code)
);
