--
-- PostgreSQL database dump
--

-- Dumped from database version 16.10
-- Dumped by pg_dump version 16.10

SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

--
-- Name: hangfire; Type: SCHEMA; Schema: -; Owner: bindatacoll
--

CREATE SCHEMA hangfire;

ALTER SCHEMA hangfire OWNER TO bindatacoll;

SET default_tablespace = '';

SET default_table_access_method = heap;

--
-- Name: aggregatedcounter; Type: TABLE; Schema: hangfire; Owner: bindatacoll
--

CREATE TABLE hangfire.aggregatedcounter (
    id bigint NOT NULL,
    key text NOT NULL,
    value bigint NOT NULL,
    expireat timestamp with time zone
);

ALTER TABLE hangfire.aggregatedcounter OWNER TO bindatacoll;

--
-- Name: aggregatedcounter_id_seq; Type: SEQUENCE; Schema: hangfire; Owner: bindatacoll
--

CREATE SEQUENCE hangfire.aggregatedcounter_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE hangfire.aggregatedcounter_id_seq OWNER TO bindatacoll;

--
-- Name: aggregatedcounter_id_seq; Type: SEQUENCE OWNED BY; Schema: hangfire; Owner: bindatacoll
--

ALTER SEQUENCE hangfire.aggregatedcounter_id_seq OWNED BY hangfire.aggregatedcounter.id;

--
-- Name: counter; Type: TABLE; Schema: hangfire; Owner: bindatacoll
--

CREATE TABLE hangfire.counter (
    id bigint NOT NULL,
    key text NOT NULL,
    value bigint NOT NULL,
    expireat timestamp with time zone
);

ALTER TABLE hangfire.counter OWNER TO bindatacoll;

--
-- Name: counter_id_seq; Type: SEQUENCE; Schema: hangfire; Owner: bindatacoll
--

CREATE SEQUENCE hangfire.counter_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE hangfire.counter_id_seq OWNER TO bindatacoll;

--
-- Name: counter_id_seq; Type: SEQUENCE OWNED BY; Schema: hangfire; Owner: bindatacoll
--

ALTER SEQUENCE hangfire.counter_id_seq OWNED BY hangfire.counter.id;

--
-- Name: hash; Type: TABLE; Schema: hangfire; Owner: bindatacoll
--

CREATE TABLE hangfire.hash (
    id bigint NOT NULL,
    key text NOT NULL,
    field text NOT NULL,
    value text,
    expireat timestamp with time zone,
    updatecount integer DEFAULT 0 NOT NULL
);

ALTER TABLE hangfire.hash OWNER TO bindatacoll;

--
-- Name: hash_id_seq; Type: SEQUENCE; Schema: hangfire; Owner: bindatacoll
--

CREATE SEQUENCE hangfire.hash_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE hangfire.hash_id_seq OWNER TO bindatacoll;

--
-- Name: hash_id_seq; Type: SEQUENCE OWNED BY; Schema: hangfire; Owner: bindatacoll
--

ALTER SEQUENCE hangfire.hash_id_seq OWNED BY hangfire.hash.id;

--
-- Name: job; Type: TABLE; Schema: hangfire; Owner: bindatacoll
--

CREATE TABLE hangfire.job (
    id bigint NOT NULL,
    stateid bigint,
    statename text,
    invocationdata jsonb NOT NULL,
    arguments jsonb NOT NULL,
    createdat timestamp with time zone NOT NULL,
    expireat timestamp with time zone,
    updatecount integer DEFAULT 0 NOT NULL
);

ALTER TABLE hangfire.job OWNER TO bindatacoll;

--
-- Name: job_id_seq; Type: SEQUENCE; Schema: hangfire; Owner: bindatacoll
--

CREATE SEQUENCE hangfire.job_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE hangfire.job_id_seq OWNER TO bindatacoll;

--
-- Name: job_id_seq; Type: SEQUENCE OWNED BY; Schema: hangfire; Owner: bindatacoll
--

ALTER SEQUENCE hangfire.job_id_seq OWNED BY hangfire.job.id;

--
-- Name: jobparameter; Type: TABLE; Schema: hangfire; Owner: bindatacoll
--

CREATE TABLE hangfire.jobparameter (
    id bigint NOT NULL,
    jobid bigint NOT NULL,
    name text NOT NULL,
    value text,
    updatecount integer DEFAULT 0 NOT NULL
);

ALTER TABLE hangfire.jobparameter OWNER TO bindatacoll;

--
-- Name: jobparameter_id_seq; Type: SEQUENCE; Schema: hangfire; Owner: bindatacoll
--

CREATE SEQUENCE hangfire.jobparameter_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE hangfire.jobparameter_id_seq OWNER TO bindatacoll;

--
-- Name: jobparameter_id_seq; Type: SEQUENCE OWNED BY; Schema: hangfire; Owner: bindatacoll
--

ALTER SEQUENCE hangfire.jobparameter_id_seq OWNED BY hangfire.jobparameter.id;

--
-- Name: jobqueue; Type: TABLE; Schema: hangfire; Owner: bindatacoll
--

CREATE TABLE hangfire.jobqueue (
    id bigint NOT NULL,
    jobid bigint NOT NULL,
    queue text NOT NULL,
    fetchedat timestamp with time zone,
    updatecount integer DEFAULT 0 NOT NULL
);

ALTER TABLE hangfire.jobqueue OWNER TO bindatacoll;

--
-- Name: jobqueue_id_seq; Type: SEQUENCE; Schema: hangfire; Owner: bindatacoll
--

CREATE SEQUENCE hangfire.jobqueue_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE hangfire.jobqueue_id_seq OWNER TO bindatacoll;

--
-- Name: jobqueue_id_seq; Type: SEQUENCE OWNED BY; Schema: hangfire; Owner: bindatacoll
--

ALTER SEQUENCE hangfire.jobqueue_id_seq OWNED BY hangfire.jobqueue.id;

--
-- Name: list; Type: TABLE; Schema: hangfire; Owner: bindatacoll
--

CREATE TABLE hangfire.list (
    id bigint NOT NULL,
    key text NOT NULL,
    value text,
    expireat timestamp with time zone,
    updatecount integer DEFAULT 0 NOT NULL
);

ALTER TABLE hangfire.list OWNER TO bindatacoll;

--
-- Name: list_id_seq; Type: SEQUENCE; Schema: hangfire; Owner: bindatacoll
--

CREATE SEQUENCE hangfire.list_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE hangfire.list_id_seq OWNER TO bindatacoll;

--
-- Name: list_id_seq; Type: SEQUENCE OWNED BY; Schema: hangfire; Owner: bindatacoll
--

ALTER SEQUENCE hangfire.list_id_seq OWNED BY hangfire.list.id;

--
-- Name: lock; Type: TABLE; Schema: hangfire; Owner: bindatacoll
--

CREATE TABLE hangfire.lock (
    resource text NOT NULL,
    updatecount integer DEFAULT 0 NOT NULL,
    acquired timestamp with time zone
);

ALTER TABLE hangfire.lock OWNER TO bindatacoll;

--
-- Name: schema; Type: TABLE; Schema: hangfire; Owner: bindatacoll
--

CREATE TABLE hangfire.schema (
    version integer NOT NULL
);

ALTER TABLE hangfire.schema OWNER TO bindatacoll;

--
-- Name: server; Type: TABLE; Schema: hangfire; Owner: bindatacoll
--

CREATE TABLE hangfire.server (
    id text NOT NULL,
    data jsonb,
    lastheartbeat timestamp with time zone NOT NULL,
    updatecount integer DEFAULT 0 NOT NULL
);

ALTER TABLE hangfire.server OWNER TO bindatacoll;

--
-- Name: set; Type: TABLE; Schema: hangfire; Owner: bindatacoll
--

CREATE TABLE hangfire.set (
    id bigint NOT NULL,
    key text NOT NULL,
    score double precision NOT NULL,
    value text NOT NULL,
    expireat timestamp with time zone,
    updatecount integer DEFAULT 0 NOT NULL
);

ALTER TABLE hangfire.set OWNER TO bindatacoll;

--
-- Name: set_id_seq; Type: SEQUENCE; Schema: hangfire; Owner: bindatacoll
--

CREATE SEQUENCE hangfire.set_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE hangfire.set_id_seq OWNER TO bindatacoll;

--
-- Name: set_id_seq; Type: SEQUENCE OWNED BY; Schema: hangfire; Owner: bindatacoll
--

ALTER SEQUENCE hangfire.set_id_seq OWNED BY hangfire.set.id;

--
-- Name: state; Type: TABLE; Schema: hangfire; Owner: bindatacoll
--

CREATE TABLE hangfire.state (
    id bigint NOT NULL,
    jobid bigint NOT NULL,
    name text NOT NULL,
    reason text,
    createdat timestamp with time zone NOT NULL,
    data jsonb,
    updatecount integer DEFAULT 0 NOT NULL
);

ALTER TABLE hangfire.state OWNER TO bindatacoll;

--
-- Name: state_id_seq; Type: SEQUENCE; Schema: hangfire; Owner: bindatacoll
--

CREATE SEQUENCE hangfire.state_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE hangfire.state_id_seq OWNER TO bindatacoll;

--
-- Name: state_id_seq; Type: SEQUENCE OWNED BY; Schema: hangfire; Owner: bindatacoll
--

ALTER SEQUENCE hangfire.state_id_seq OWNED BY hangfire.state.id;

--
-- Name: aggregatedcounter id; Type: DEFAULT; Schema: hangfire; Owner: bindatacoll
--

ALTER TABLE ONLY hangfire.aggregatedcounter ALTER COLUMN id SET DEFAULT nextval('hangfire.aggregatedcounter_id_seq'::regclass);

--
-- Name: counter id; Type: DEFAULT; Schema: hangfire; Owner: bindatacoll
--

ALTER TABLE ONLY hangfire.counter ALTER COLUMN id SET DEFAULT nextval('hangfire.counter_id_seq'::regclass);

--
-- Name: hash id; Type: DEFAULT; Schema: hangfire; Owner: bindatacoll
--

ALTER TABLE ONLY hangfire.hash ALTER COLUMN id SET DEFAULT nextval('hangfire.hash_id_seq'::regclass);

--
-- Name: job id; Type: DEFAULT; Schema: hangfire; Owner: bindatacoll
--

ALTER TABLE ONLY hangfire.job ALTER COLUMN id SET DEFAULT nextval('hangfire.job_id_seq'::regclass);

--
-- Name: jobparameter id; Type: DEFAULT; Schema: hangfire; Owner: bindatacoll
--

ALTER TABLE ONLY hangfire.jobparameter ALTER COLUMN id SET DEFAULT nextval('hangfire.jobparameter_id_seq'::regclass);

--
-- Name: jobqueue id; Type: DEFAULT; Schema: hangfire; Owner: bindatacoll
--

ALTER TABLE ONLY hangfire.jobqueue ALTER COLUMN id SET DEFAULT nextval('hangfire.jobqueue_id_seq'::regclass);

--
-- Name: list id; Type: DEFAULT; Schema: hangfire; Owner: bindatacoll
--

ALTER TABLE ONLY hangfire.list ALTER COLUMN id SET DEFAULT nextval('hangfire.list_id_seq'::regclass);

--
-- Name: set id; Type: DEFAULT; Schema: hangfire; Owner: bindatacoll
--

ALTER TABLE ONLY hangfire.set ALTER COLUMN id SET DEFAULT nextval('hangfire.set_id_seq'::regclass);

--
-- Name: state id; Type: DEFAULT; Schema: hangfire; Owner: bindatacoll
--

ALTER TABLE ONLY hangfire.state ALTER COLUMN id SET DEFAULT nextval('hangfire.state_id_seq'::regclass);

--
-- Name: aggregatedcounter aggregatedcounter_key_key; Type: CONSTRAINT; Schema: hangfire; Owner: bindatacoll
--

ALTER TABLE ONLY hangfire.aggregatedcounter
    ADD CONSTRAINT aggregatedcounter_key_key UNIQUE (key);

--
-- Name: aggregatedcounter aggregatedcounter_pkey; Type: CONSTRAINT; Schema: hangfire; Owner: bindatacoll
--

ALTER TABLE ONLY hangfire.aggregatedcounter
    ADD CONSTRAINT aggregatedcounter_pkey PRIMARY KEY (id);

--
-- Name: counter counter_pkey; Type: CONSTRAINT; Schema: hangfire; Owner: bindatacoll
--

ALTER TABLE ONLY hangfire.counter
    ADD CONSTRAINT counter_pkey PRIMARY KEY (id);

--
-- Name: hash hash_key_field_key; Type: CONSTRAINT; Schema: hangfire; Owner: bindatacoll
--

ALTER TABLE ONLY hangfire.hash
    ADD CONSTRAINT hash_key_field_key UNIQUE (key, field);

--
-- Name: hash hash_pkey; Type: CONSTRAINT; Schema: hangfire; Owner: bindatacoll
--

ALTER TABLE ONLY hangfire.hash
    ADD CONSTRAINT hash_pkey PRIMARY KEY (id);

--
-- Name: job job_pkey; Type: CONSTRAINT; Schema: hangfire; Owner: bindatacoll
--

ALTER TABLE ONLY hangfire.job
    ADD CONSTRAINT job_pkey PRIMARY KEY (id);

--
-- Name: jobparameter jobparameter_pkey; Type: CONSTRAINT; Schema: hangfire; Owner: bindatacoll
--

ALTER TABLE ONLY hangfire.jobparameter
    ADD CONSTRAINT jobparameter_pkey PRIMARY KEY (id);

--
-- Name: jobqueue jobqueue_pkey; Type: CONSTRAINT; Schema: hangfire; Owner: bindatacoll
--

ALTER TABLE ONLY hangfire.jobqueue
    ADD CONSTRAINT jobqueue_pkey PRIMARY KEY (id);

--
-- Name: list list_pkey; Type: CONSTRAINT; Schema: hangfire; Owner: bindatacoll
--

ALTER TABLE ONLY hangfire.list
    ADD CONSTRAINT list_pkey PRIMARY KEY (id);

--
-- Name: lock lock_resource_key; Type: CONSTRAINT; Schema: hangfire; Owner: bindatacoll
--

ALTER TABLE ONLY hangfire.lock
    ADD CONSTRAINT lock_resource_key UNIQUE (resource);

ALTER TABLE ONLY hangfire.lock REPLICA IDENTITY USING INDEX lock_resource_key;

--
-- Name: schema schema_pkey; Type: CONSTRAINT; Schema: hangfire; Owner: bindatacoll
--

ALTER TABLE ONLY hangfire.schema
    ADD CONSTRAINT schema_pkey PRIMARY KEY (version);

--
-- Name: server server_pkey; Type: CONSTRAINT; Schema: hangfire; Owner: bindatacoll
--

ALTER TABLE ONLY hangfire.server
    ADD CONSTRAINT server_pkey PRIMARY KEY (id);

--
-- Name: set set_key_value_key; Type: CONSTRAINT; Schema: hangfire; Owner: bindatacoll
--

ALTER TABLE ONLY hangfire.set
    ADD CONSTRAINT set_key_value_key UNIQUE (key, value);

--
-- Name: set set_pkey; Type: CONSTRAINT; Schema: hangfire; Owner: bindatacoll
--

ALTER TABLE ONLY hangfire.set
    ADD CONSTRAINT set_pkey PRIMARY KEY (id);

--
-- Name: state state_pkey; Type: CONSTRAINT; Schema: hangfire; Owner: bindatacoll
--

ALTER TABLE ONLY hangfire.state
    ADD CONSTRAINT state_pkey PRIMARY KEY (id);

--
-- Name: ix_hangfire_counter_expireat; Type: INDEX; Schema: hangfire; Owner: bindatacoll
--

CREATE INDEX ix_hangfire_counter_expireat ON hangfire.counter USING btree (expireat);

--
-- Name: ix_hangfire_counter_key; Type: INDEX; Schema: hangfire; Owner: bindatacoll
--

CREATE INDEX ix_hangfire_counter_key ON hangfire.counter USING btree (key);

--
-- Name: ix_hangfire_hash_expireat; Type: INDEX; Schema: hangfire; Owner: bindatacoll
--

CREATE INDEX ix_hangfire_hash_expireat ON hangfire.hash USING btree (expireat);

--
-- Name: ix_hangfire_job_expireat; Type: INDEX; Schema: hangfire; Owner: bindatacoll
--

CREATE INDEX ix_hangfire_job_expireat ON hangfire.job USING btree (expireat);

--
-- Name: ix_hangfire_job_statename; Type: INDEX; Schema: hangfire; Owner: bindatacoll
--

CREATE INDEX ix_hangfire_job_statename ON hangfire.job USING btree (statename);

--
-- Name: ix_hangfire_job_statename_is_not_null; Type: INDEX; Schema: hangfire; Owner: bindatacoll
--

CREATE INDEX ix_hangfire_job_statename_is_not_null ON hangfire.job USING btree (statename) INCLUDE (id) WHERE (statename IS NOT NULL);

--
-- Name: ix_hangfire_jobparameter_jobidandname; Type: INDEX; Schema: hangfire; Owner: bindatacoll
--

CREATE INDEX ix_hangfire_jobparameter_jobidandname ON hangfire.jobparameter USING btree (jobid, name);

--
-- Name: ix_hangfire_jobqueue_fetchedat_queue_jobid; Type: INDEX; Schema: hangfire; Owner: bindatacoll
--

CREATE INDEX ix_hangfire_jobqueue_fetchedat_queue_jobid ON hangfire.jobqueue USING btree (fetchedat NULLS FIRST, queue, jobid);

--
-- Name: ix_hangfire_jobqueue_jobidandqueue; Type: INDEX; Schema: hangfire; Owner: bindatacoll
--

CREATE INDEX ix_hangfire_jobqueue_jobidandqueue ON hangfire.jobqueue USING btree (jobid, queue);

--
-- Name: ix_hangfire_jobqueue_queueandfetchedat; Type: INDEX; Schema: hangfire; Owner: bindatacoll
--

CREATE INDEX ix_hangfire_jobqueue_queueandfetchedat ON hangfire.jobqueue USING btree (queue, fetchedat);


--
-- Name: ix_hangfire_list_expireat; Type: INDEX; Schema: hangfire; Owner: bindatacoll
--

CREATE INDEX ix_hangfire_list_expireat ON hangfire.list USING btree (expireat);


--
-- Name: ix_hangfire_set_expireat; Type: INDEX; Schema: hangfire; Owner: bindatacoll
--

CREATE INDEX ix_hangfire_set_expireat ON hangfire.set USING btree (expireat);


--
-- Name: ix_hangfire_set_key_score; Type: INDEX; Schema: hangfire; Owner: bindatacoll
--

CREATE INDEX ix_hangfire_set_key_score ON hangfire.set USING btree (key, score);


--
-- Name: ix_hangfire_state_jobid; Type: INDEX; Schema: hangfire; Owner: bindatacoll
--

CREATE INDEX ix_hangfire_state_jobid ON hangfire.state USING btree (jobid);


--
-- Name: jobparameter jobparameter_jobid_fkey; Type: FK CONSTRAINT; Schema: hangfire; Owner: bindatacoll
--

ALTER TABLE ONLY hangfire.jobparameter
    ADD CONSTRAINT jobparameter_jobid_fkey FOREIGN KEY (jobid) REFERENCES hangfire.job(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: state state_jobid_fkey; Type: FK CONSTRAINT; Schema: hangfire; Owner: bindatacoll
--

ALTER TABLE ONLY hangfire.state
    ADD CONSTRAINT state_jobid_fkey FOREIGN KEY (jobid) REFERENCES hangfire.job(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- PostgreSQL database dump complete
--