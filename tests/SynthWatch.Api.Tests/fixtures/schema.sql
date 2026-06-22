--
-- PostgreSQL database dump
--


-- Dumped from database version 16.14
-- Dumped by pg_dump version 18.4


--
-- Name: public; Type: SCHEMA; Schema: -; Owner: -
--

-- *not* creating schema, since initdb creates it


--
-- Name: sla_availability(timestamp with time zone, timestamp with time zone); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sla_availability(p_from timestamp with time zone, p_to timestamp with time zone) RETURNS TABLE(check_id bigint, check_name text, kind text, window_from timestamp with time zone, window_to timestamp with time zone, completed_runs bigint, up_runs bigint, down_runs bigint, availability_pct numeric)
    LANGUAGE sql STABLE
    AS $$
    SELECT
        c.id   AS check_id,
        c.name AS check_name,
        c.kind AS kind,
        p_from AS window_from,
        p_to   AS window_to,
        count(*) FILTER (WHERE r.status IN ('pass', 'warn', 'fail', 'error')) AS completed_runs,
        count(*) FILTER (WHERE r.status IN ('pass', 'warn'))                  AS up_runs,
        count(*) FILTER (WHERE r.status IN ('fail', 'error'))                 AS down_runs,
        round(
            100.0 * count(*) FILTER (WHERE r.status IN ('pass', 'warn'))
                  / nullif(count(*) FILTER (WHERE r.status IN ('pass', 'warn', 'fail', 'error')), 0),
            4
        ) AS availability_pct
    FROM checks c
    LEFT JOIN runs r
           ON r.check_id   = c.id
          AND r.started_at >= p_from
          AND r.started_at <  p_to
    -- MAINTENANCE-WINDOW EXCLUSION (additive anti-join): a run is dropped if it
    -- falls inside an active window for this check (check_id = c.id) OR a
    -- fleet-wide window (check_id IS NULL). Runs not covered keep mw.id NULL and
    -- survive the WHERE; checks with no runs keep their single null-run row.
    LEFT JOIN maintenance_windows mw
           ON (mw.check_id = c.id OR mw.check_id IS NULL)
          AND r.started_at >= mw.starts_at
          AND r.started_at <  mw.ends_at
    WHERE mw.id IS NULL
    GROUP BY c.id, c.name, c.kind
$$;




--
-- Name: alert_profiles; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.alert_profiles (
    id bigint NOT NULL,
    name text NOT NULL,
    rules jsonb DEFAULT '[]'::jsonb NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: alert_profiles_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.alert_profiles ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.alert_profiles_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: checks; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.checks (
    id bigint NOT NULL,
    name text NOT NULL,
    kind text NOT NULL,
    target_url text NOT NULL,
    flow_name text,
    method text DEFAULT 'GET'::text NOT NULL,
    expected_status integer DEFAULT 200 NOT NULL,
    body_must_contain text,
    interval_seconds integer DEFAULT 300 NOT NULL,
    last_run_at timestamp with time zone,
    timeout_ms integer DEFAULT 30000 NOT NULL,
    failure_threshold integer DEFAULT 3 NOT NULL,
    severity text DEFAULT 'critical'::text NOT NULL,
    enabled boolean DEFAULT true NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    lighthouse_enabled boolean DEFAULT false NOT NULL,
    lighthouse_interval_seconds integer,
    lighthouse_form_factor text DEFAULT 'desktop'::text NOT NULL,
    perf_budget_lcp_ms integer,
    perf_budget_transfer_bytes bigint,
    cert_expiry_warn_days integer DEFAULT 30 NOT NULL,
    alert_profile_id bigint,
    last_warn_notified_at timestamp with time zone,
    warn_renotify_seconds integer DEFAULT 86400 NOT NULL,
    assertions jsonb DEFAULT '[]'::jsonb NOT NULL,
    request_headers jsonb,
    request_body text,
    auth jsonb,
    net_config jsonb,
    steps jsonb,
    CONSTRAINT browser_needs_flow CHECK (((kind <> 'browser'::text) OR (flow_name IS NOT NULL))),
    CONSTRAINT checks_failure_threshold_check CHECK ((failure_threshold > 0)),
    CONSTRAINT checks_interval_seconds_check CHECK ((interval_seconds > 0)),
    CONSTRAINT checks_kind_check CHECK ((kind = ANY (ARRAY['http'::text, 'browser'::text, 'ssl'::text, 'dns'::text, 'tcp'::text, 'ping'::text, 'multistep'::text]))),
    CONSTRAINT checks_severity_check CHECK ((severity = ANY (ARRAY['critical'::text, 'warning'::text]))),
    CONSTRAINT checks_timeout_ms_check CHECK ((timeout_ms > 0)),
    CONSTRAINT checks_warn_renotify_seconds_check CHECK ((warn_renotify_seconds > 0))
);


--
-- Name: checks_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.checks ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.checks_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: flow_manifest; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.flow_manifest (
    name text NOT NULL,
    description text,
    entry_url_hint text,
    updated_at timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: incidents; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.incidents (
    id bigint NOT NULL,
    check_id bigint NOT NULL,
    status text NOT NULL,
    severity text NOT NULL,
    opened_at timestamp with time zone DEFAULT now() NOT NULL,
    resolved_at timestamp with time zone,
    opened_run_id bigint,
    resolved_run_id bigint,
    consecutive_failures integer DEFAULT 0 NOT NULL,
    summary text,
    CONSTRAINT incidents_severity_check CHECK ((severity = ANY (ARRAY['critical'::text, 'warning'::text]))),
    CONSTRAINT incidents_status_check CHECK ((status = ANY (ARRAY['open'::text, 'resolved'::text])))
);


--
-- Name: incidents_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.incidents ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.incidents_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: maintenance_windows; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.maintenance_windows (
    id bigint NOT NULL,
    check_id bigint,
    starts_at timestamp with time zone NOT NULL,
    ends_at timestamp with time zone NOT NULL,
    reason text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT maintenance_windows_valid_range CHECK ((ends_at > starts_at))
);


--
-- Name: maintenance_windows_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.maintenance_windows ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.maintenance_windows_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: run_metrics; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.run_metrics (
    id bigint NOT NULL,
    run_id bigint NOT NULL,
    ttfb_ms integer,
    dom_content_loaded_ms integer,
    load_event_ms integer,
    fcp_ms integer,
    lcp_ms integer,
    transfer_bytes bigint,
    resource_count integer,
    dom_node_count integer,
    js_heap_bytes bigint,
    cpu_time_ms integer,
    layout_count integer,
    recalc_style_count integer,
    captured_at timestamp with time zone DEFAULT now() NOT NULL,
    cls double precision,
    inp_ms integer
);


--
-- Name: run_metrics_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.run_metrics ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.run_metrics_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: run_steps; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.run_steps (
    id bigint NOT NULL,
    run_id bigint NOT NULL,
    step_index integer NOT NULL,
    name text NOT NULL,
    status text NOT NULL,
    duration_ms integer NOT NULL,
    error_message text,
    started_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT run_steps_status_check CHECK ((status = ANY (ARRAY['pass'::text, 'fail'::text, 'error'::text])))
);


--
-- Name: run_steps_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.run_steps ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.run_steps_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: runs; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.runs (
    id bigint NOT NULL,
    check_id bigint NOT NULL,
    status text DEFAULT 'running'::text NOT NULL,
    started_at timestamp with time zone DEFAULT now() NOT NULL,
    finished_at timestamp with time zone,
    duration_ms integer,
    http_status integer,
    error_message text,
    failed_step text,
    screenshot_url text,
    cert_days_remaining integer,
    trace_url text,
    location text DEFAULT 'default'::text,
    CONSTRAINT runs_status_check CHECK ((status = ANY (ARRAY['pass'::text, 'warn'::text, 'fail'::text, 'error'::text, 'running'::text])))
);


--
-- Name: runs_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.runs ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.runs_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: schema_migrations; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.schema_migrations (
    version text NOT NULL,
    applied_at timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: sla_availability_24h; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.sla_availability_24h AS
 SELECT check_id,
    check_name,
    kind,
    window_from,
    window_to,
    completed_runs,
    up_runs,
    down_runs,
    availability_pct
   FROM public.sla_availability((now() - '24:00:00'::interval), now()) sla_availability(check_id, check_name, kind, window_from, window_to, completed_runs, up_runs, down_runs, availability_pct);


--
-- Name: sla_availability_30d; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.sla_availability_30d AS
 SELECT check_id,
    check_name,
    kind,
    window_from,
    window_to,
    completed_runs,
    up_runs,
    down_runs,
    availability_pct
   FROM public.sla_availability((now() - '30 days'::interval), now()) sla_availability(check_id, check_name, kind, window_from, window_to, completed_runs, up_runs, down_runs, availability_pct);


--
-- Name: sla_availability_7d; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.sla_availability_7d AS
 SELECT check_id,
    check_name,
    kind,
    window_from,
    window_to,
    completed_runs,
    up_runs,
    down_runs,
    availability_pct
   FROM public.sla_availability((now() - '7 days'::interval), now()) sla_availability(check_id, check_name, kind, window_from, window_to, completed_runs, up_runs, down_runs, availability_pct);


--
-- Name: alert_profiles alert_profiles_name_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.alert_profiles
    ADD CONSTRAINT alert_profiles_name_key UNIQUE (name);


--
-- Name: alert_profiles alert_profiles_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.alert_profiles
    ADD CONSTRAINT alert_profiles_pkey PRIMARY KEY (id);


--
-- Name: checks checks_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.checks
    ADD CONSTRAINT checks_pkey PRIMARY KEY (id);


--
-- Name: flow_manifest flow_manifest_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.flow_manifest
    ADD CONSTRAINT flow_manifest_pkey PRIMARY KEY (name);


--
-- Name: incidents incidents_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.incidents
    ADD CONSTRAINT incidents_pkey PRIMARY KEY (id);


--
-- Name: maintenance_windows maintenance_windows_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.maintenance_windows
    ADD CONSTRAINT maintenance_windows_pkey PRIMARY KEY (id);


--
-- Name: run_metrics run_metrics_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.run_metrics
    ADD CONSTRAINT run_metrics_pkey PRIMARY KEY (id);


--
-- Name: run_metrics run_metrics_run_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.run_metrics
    ADD CONSTRAINT run_metrics_run_id_key UNIQUE (run_id);


--
-- Name: run_steps run_steps_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.run_steps
    ADD CONSTRAINT run_steps_pkey PRIMARY KEY (id);


--
-- Name: runs runs_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.runs
    ADD CONSTRAINT runs_pkey PRIMARY KEY (id);


--
-- Name: schema_migrations schema_migrations_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.schema_migrations
    ADD CONSTRAINT schema_migrations_pkey PRIMARY KEY (version);


--
-- Name: maintenance_windows_span_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX maintenance_windows_span_idx ON public.maintenance_windows USING btree (starts_at, ends_at);


--
-- Name: one_open_incident_per_check; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX one_open_incident_per_check ON public.incidents USING btree (check_id) WHERE (status = 'open'::text);


--
-- Name: run_steps_run_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX run_steps_run_idx ON public.run_steps USING btree (run_id, step_index);


--
-- Name: runs_check_started_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX runs_check_started_idx ON public.runs USING btree (check_id, started_at DESC);


--
-- Name: checks checks_alert_profile_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.checks
    ADD CONSTRAINT checks_alert_profile_id_fkey FOREIGN KEY (alert_profile_id) REFERENCES public.alert_profiles(id) ON DELETE SET NULL;


--
-- Name: incidents incidents_check_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.incidents
    ADD CONSTRAINT incidents_check_id_fkey FOREIGN KEY (check_id) REFERENCES public.checks(id) ON DELETE CASCADE;


--
-- Name: incidents incidents_opened_run_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.incidents
    ADD CONSTRAINT incidents_opened_run_id_fkey FOREIGN KEY (opened_run_id) REFERENCES public.runs(id);


--
-- Name: incidents incidents_resolved_run_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.incidents
    ADD CONSTRAINT incidents_resolved_run_id_fkey FOREIGN KEY (resolved_run_id) REFERENCES public.runs(id);


--
-- Name: maintenance_windows maintenance_windows_check_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.maintenance_windows
    ADD CONSTRAINT maintenance_windows_check_id_fkey FOREIGN KEY (check_id) REFERENCES public.checks(id) ON DELETE CASCADE;


--
-- Name: run_metrics run_metrics_run_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.run_metrics
    ADD CONSTRAINT run_metrics_run_id_fkey FOREIGN KEY (run_id) REFERENCES public.runs(id) ON DELETE CASCADE;


--
-- Name: run_steps run_steps_run_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.run_steps
    ADD CONSTRAINT run_steps_run_id_fkey FOREIGN KEY (run_id) REFERENCES public.runs(id) ON DELETE CASCADE;


--
-- Name: runs runs_check_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.runs
    ADD CONSTRAINT runs_check_id_fkey FOREIGN KEY (check_id) REFERENCES public.checks(id) ON DELETE CASCADE;


--
-- PostgreSQL database dump complete
--


