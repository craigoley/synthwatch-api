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
    slo_target real,
    request_headers jsonb,
    request_body text,
    auth jsonb,
    net_config jsonb,
    steps jsonb,
    source_key text,
    spec_path text,
    CONSTRAINT browser_needs_flow CHECK (((kind <> 'browser'::text) OR (flow_name IS NOT NULL))),
    CONSTRAINT checks_failure_threshold_check CHECK ((failure_threshold > 0)),
    CONSTRAINT checks_interval_seconds_check CHECK ((interval_seconds > 0)),
    CONSTRAINT checks_kind_check CHECK ((kind = ANY (ARRAY['http'::text, 'browser'::text, 'ssl'::text, 'dns'::text, 'tcp'::text, 'ping'::text, 'multistep'::text]))),
    CONSTRAINT checks_severity_check CHECK ((severity = ANY (ARRAY['critical'::text, 'warning'::text]))),
    CONSTRAINT checks_spec_path_shape CHECK ((spec_path IS NULL OR ((spec_path ~ '^monitors/.+\.spec\.ts$') AND ("position"(spec_path, '..'::text) = 0)))),
    CONSTRAINT checks_timeout_ms_check CHECK ((timeout_ms > 0)),
    CONSTRAINT checks_warn_renotify_seconds_check CHECK ((warn_renotify_seconds > 0))
);

-- One live check per manifest id (mirrors checks_source_key_uniq; NULL-tolerant).
CREATE UNIQUE INDEX checks_source_key_uniq ON public.checks (source_key) WHERE source_key IS NOT NULL;


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
    rca jsonb,
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
-- Name: incidents_opened_idx; Type: INDEX; Schema: public; Owner: -
-- Backs the GET /api/incidents keyset cursor (opened_at DESC, id DESC) over a date-range window —
-- the incidents equivalent of runs_check_started_idx. ★ RUNNER-OWNED SCHEMA: production needs the
-- matching migration in synthwatch-monitors (CREATE INDEX CONCURRENTLY); this fixture row exercises
-- the indexed path in tests until that migration lands.
--

CREATE INDEX incidents_opened_idx ON public.incidents USING btree (opened_at DESC, id DESC);


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



-- SLO error-budget/burn (migration 0016). Added to the test snapshot (predates 0016).
CREATE OR REPLACE FUNCTION public.slo_status(p_check_id bigint, p_from timestamp with time zone, p_to timestamp with time zone)
 RETURNS TABLE(check_id bigint, slo_target real, window_from timestamp with time zone, window_to timestamp with time zone, total_runs bigint, down_runs bigint, budget numeric, consumed bigint, remaining numeric, remaining_pct numeric, burn_rate numeric)
 LANGUAGE sql
 STABLE
AS $function$
    WITH agg AS (
        SELECT
            c.id         AS check_id,
            c.slo_target AS slo_target,
            count(*) FILTER (WHERE r.status IN ('pass', 'warn', 'fail', 'error')) AS total_runs,
            count(*) FILTER (WHERE r.status IN ('fail', 'error'))                 AS down_runs
        FROM checks c
        LEFT JOIN runs r
               ON r.check_id   = c.id
              AND r.started_at >= p_from
              AND r.started_at <  p_to
        -- Maintenance-window anti-join (mirrors sla_availability): drop runs inside
        -- an active window for this check OR a fleet-wide window.
        LEFT JOIN maintenance_windows mw
               ON (mw.check_id = c.id OR mw.check_id IS NULL)
              AND r.started_at >= mw.starts_at
              AND r.started_at <  mw.ends_at
        WHERE c.id = p_check_id
          AND c.slo_target IS NOT NULL   -- opt-in: no target => no rows
          AND mw.id IS NULL
        GROUP BY c.id, c.slo_target
    )
    SELECT
        check_id,
        slo_target,
        p_from AS window_from,
        p_to   AS window_to,
        total_runs,
        down_runs,
        (1::numeric - slo_target::numeric) * total_runs                         AS budget,
        down_runs                                                      AS consumed,
        (1::numeric - slo_target::numeric) * total_runs - down_runs             AS remaining,
        CASE WHEN (1::numeric - slo_target::numeric) * total_runs > 0
             THEN round(1 - down_runs::numeric / ((1::numeric - slo_target::numeric) * total_runs), 6)
             END                                                       AS remaining_pct,
        CASE WHEN total_runs > 0
             THEN round((down_runs::numeric / total_runs) / (1::numeric - slo_target::numeric), 4)
             ELSE 0 END                                                AS burn_rate
    FROM agg
$function$
;

-- sla_availability_90d view (migration 0018). Added to the test snapshot (predates 0018);
-- identical to _30d with a 90-day interval.
CREATE VIEW public.sla_availability_90d AS
 SELECT check_id,
    check_name,
    kind,
    window_from,
    window_to,
    completed_runs,
    up_runs,
    down_runs,
    availability_pct
   FROM public.sla_availability((now() - '90 days'::interval), now()) sla_availability(check_id, check_name, kind, window_from, window_to, completed_runs, up_runs, down_runs, availability_pct);


--
-- Multi-location (runner migration #73 / 4-MLACT step 1): the `locations` registry + per-check cadence
-- cursors. Added to this test snapshot (the pg_dump predates #73) so the API create-path cursor seeding
-- — assignDefaultLocations() replicated in C# — runs against the same shape as prod. Seeded 'default'
-- active, matching #73.
--
CREATE TABLE public.locations (
    name text NOT NULL,
    enabled boolean DEFAULT true NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT locations_pkey PRIMARY KEY (name)
);

INSERT INTO public.locations (name, enabled) VALUES ('default', true);

CREATE TABLE public.check_locations (
    check_id bigint NOT NULL,
    location text NOT NULL,
    last_run_at timestamp with time zone,
    CONSTRAINT check_locations_pkey PRIMARY KEY (check_id, location),
    CONSTRAINT check_locations_check_id_fkey FOREIGN KEY (check_id) REFERENCES public.checks(id) ON DELETE CASCADE
);


--
-- Dashboard-managed alerting (runner migration 0023 / #81): delivery channels + routing. Added to the
-- test snapshot to mirror the live schema (channel_id FK is ON DELETE CASCADE — the API enforces the
-- delete-referenced-channel 409 guard in code, not the DB). Seeded like #81 (email/webhook + critical/
-- warning routes) so tests exercise the assembled routing shape.
--
CREATE TABLE public.channels (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name       text NOT NULL UNIQUE,
    type       text NOT NULL,
    config     jsonb NOT NULL DEFAULT '{}'::jsonb,
    enabled    boolean NOT NULL DEFAULT true,
    created_at timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT channels_type_check CHECK (type = ANY (ARRAY['email'::text, 'webhook'::text]))
);

CREATE TABLE public.alert_routes (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    severity   text,
    check_id   bigint REFERENCES public.checks(id) ON DELETE CASCADE,
    channel_id bigint NOT NULL REFERENCES public.channels(id) ON DELETE CASCADE,
    created_at timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT alert_route_one_dimension CHECK (severity IS NOT NULL AND check_id IS NULL OR severity IS NULL AND check_id IS NOT NULL),
    CONSTRAINT alert_routes_severity_check CHECK (severity IS NULL OR (severity = ANY (ARRAY['critical'::text, 'warning'::text])))
);
CREATE UNIQUE INDEX alert_routes_check_uq ON public.alert_routes (check_id, channel_id) WHERE check_id IS NOT NULL;
CREATE UNIQUE INDEX alert_routes_severity_uq ON public.alert_routes (severity, channel_id) WHERE check_id IS NULL;

INSERT INTO public.channels (name, type) VALUES ('email', 'email'), ('webhook', 'webhook');
INSERT INTO public.alert_routes (severity, channel_id)
  SELECT s, c.id FROM (VALUES ('critical'), ('warning')) v(s) CROSS JOIN public.channels c;


--
-- Channel test-send queue (runner migration 0026): the API INSERTs a 'pending' row per
-- POST /api/channels/{id}/test + READs status; the RUNNER drains it through the real dispatch path and
-- advances status pending -> sending -> delivered|failed. Mirrors the live schema (channel_id FK CASCADE;
-- status CHECK; requested_at DEFAULT now()). Kept in sync by hand with the runner's 0026 migration — the
-- rest of this file is a pg_dump snapshot of the runner DB; this block matches that table's definition.
--
CREATE TABLE public.test_send_requests (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    channel_id   bigint NOT NULL REFERENCES public.channels(id) ON DELETE CASCADE,
    status       text NOT NULL DEFAULT 'pending',
    detail       text,
    requested_at timestamp with time zone NOT NULL DEFAULT now(),
    completed_at timestamp with time zone,
    CONSTRAINT test_send_requests_status_check CHECK (status = ANY (ARRAY['pending'::text, 'sending'::text, 'delivered'::text, 'failed'::text]))
);
CREATE INDEX test_send_requests_channel_idx ON public.test_send_requests (channel_id);


--
-- Tags (runner migration 0024 / #84): normalized key:value tags on checks. Added to the test snapshot
-- to mirror the live schema (PK (check_id,key) = one value per key; lowercase/whitespace-free CHECKs;
-- value non-empty; key may be ''; FK CASCADE).
--
CREATE TABLE public.check_tags (
    check_id bigint NOT NULL REFERENCES public.checks(id) ON DELETE CASCADE,
    key      text NOT NULL DEFAULT ''::text,
    value    text NOT NULL,
    CONSTRAINT check_tags_pkey PRIMARY KEY (check_id, key),
    CONSTRAINT check_tags_key_check CHECK (key = lower(key) AND key !~ '[[:space:]]'::text),
    CONSTRAINT check_tags_value_check CHECK (value <> ''::text AND value = lower(value) AND value !~ '[[:space:]]'::text)
);
CREATE INDEX check_tags_key_value_idx ON public.check_tags (key, value);


--
-- Tag-routing (runner migration 0025 / #85): tag-rule routing dimension (severity ∪ per-check ∪ tag-rules
-- UNION dispatch). Added to the snapshot to mirror the live schema (UNIQUE (tag_key,tag_value,channel_id);
-- normalized CHECKs matching check_tags; channel_id FK CASCADE; index).
--
CREATE TABLE public.tag_routes (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tag_key    text NOT NULL,
    tag_value  text NOT NULL,
    channel_id bigint NOT NULL REFERENCES public.channels(id) ON DELETE CASCADE,
    created_at timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT tag_routes_tag_key_check CHECK (tag_key = lower(tag_key) AND tag_key !~ '[[:space:]]'::text),
    CONSTRAINT tag_routes_tag_value_check CHECK (tag_value <> ''::text AND tag_value = lower(tag_value) AND tag_value !~ '[[:space:]]'::text),
    CONSTRAINT tag_routes_uq UNIQUE (tag_key, tag_value, channel_id)
);
CREATE INDEX tag_routes_key_value_idx ON public.tag_routes (tag_key, tag_value);


--
-- Reporting Layer 1 (runner migration 0028 / #88): daily_check_rollup — one row per check per UTC day.
-- Added to the snapshot to mirror the live schema so the report endpoints aggregate against the prod
-- shape. (Availability is additive from these counts; multi-day percentiles are recomputed from raw runs.)
--
CREATE TABLE public.daily_check_rollup (
    check_id           bigint NOT NULL REFERENCES public.checks(id) ON DELETE CASCADE,
    day                date NOT NULL,
    up_count           integer NOT NULL DEFAULT 0,
    down_count         integer NOT NULL DEFAULT 0,
    total_count        integer NOT NULL DEFAULT 0,
    availability_pct   numeric,
    latency_count      integer NOT NULL DEFAULT 0,
    duration_avg_ms    numeric,
    duration_p50_ms    integer,
    duration_p95_ms    integer,
    duration_p99_ms    integer,
    duration_min_ms    integer,
    duration_max_ms    integer,
    vitals_count       integer NOT NULL DEFAULT 0,
    lcp_avg_ms         numeric,
    lcp_p75_ms         integer,
    fcp_avg_ms         numeric,
    fcp_p75_ms         integer,
    ttfb_avg_ms        numeric,
    ttfb_p75_ms        integer,
    cls_avg            double precision,
    cls_p75            double precision,
    load_event_avg_ms  numeric,
    transfer_bytes_avg bigint,
    incidents_opened   integer NOT NULL DEFAULT 0,
    downtime_minutes   numeric NOT NULL DEFAULT 0,
    computed_at        timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT daily_check_rollup_pkey PRIMARY KEY (check_id, day)
);
CREATE INDEX daily_check_rollup_day_idx ON public.daily_check_rollup (day);


--
-- Reporting Layer 3 (runner migration 0029, LIVE): report_narratives. The runner generates these (it owns
-- the AOAI plumbing); the API serves the latest row read-only. ★ Mirrors \d report_narratives EXACTLY —
-- the column is "window" (quoted: it's a Postgres reserved word). PK uses "window".
--
CREATE TABLE public.report_narratives (
    scope_type   text NOT NULL,                          -- 'fleet' | 'monitor'
    scope_key    text NOT NULL,                          -- 'fleet' sentinel | check id (text)
    "window"     text NOT NULL,                           -- '7d' | '30d' | '90d' (reserved word → quoted)
    generated_at timestamp with time zone NOT NULL DEFAULT now(),
    headline     text,
    body         text,
    highlights   jsonb NOT NULL DEFAULT '[]'::jsonb,      -- string[]
    model        text,
    fact_pack    jsonb NOT NULL,                          -- the cited-numbers fact pack
    CONSTRAINT report_narratives_scope_type_check CHECK (scope_type = ANY (ARRAY['fleet'::text, 'monitor'::text])),
    CONSTRAINT report_narratives_pkey PRIMARY KEY (scope_type, scope_key, "window")
);


--
-- Monitors-as-code drift (runner migration 0031, Phase 6b): reconcile_drift. The reconcile job diffs the
-- synthwatch-monitors manifest against live checks READ-ONLY and UPSERTs the current drift set (one row per
-- (source_key, drift_type)); the API serves the latest snapshot read-only. detail jsonb shape varies by
-- drift_type (a 'changed' row carries the per-field before/after diff). Mirrors \d reconcile_drift.
--
CREATE TABLE public.reconcile_drift (
    source_key  text NOT NULL,
    drift_type  text NOT NULL
                CONSTRAINT reconcile_drift_drift_type_check
                CHECK (drift_type = ANY (ARRAY['new'::text, 'changed'::text, 'missing'::text, 'orphan'::text])),
    detail      jsonb NOT NULL DEFAULT '{}'::jsonb,
    detected_at timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT reconcile_drift_pkey PRIMARY KEY (source_key, drift_type)
);

--
-- Manifest-snapshot inventory (runner migration 0036, Phase 13): spec_catalog. The reconcile job
-- snapshots every manifest spec here (full reload each run) + its runnability probe result, so the API
-- can serve the read-only catalog (GET /api/specs = spec_catalog LEFT JOIN checks). Mirrors \d spec_catalog.
--
CREATE TABLE public.spec_catalog (
    source_key                 text NOT NULL,
    name                       text NOT NULL,
    spec_path                  text NOT NULL,
    kind                       text NOT NULL,
    target                     text,
    suggested_interval_seconds integer,
    tags                       jsonb NOT NULL DEFAULT '[]'::jsonb,
    description                text,
    enabled_by_default         boolean NOT NULL DEFAULT false,
    runnable                   boolean NOT NULL,
    not_runnable_reason        text,
    probed_at                  timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT spec_catalog_pkey PRIMARY KEY (source_key)
);
