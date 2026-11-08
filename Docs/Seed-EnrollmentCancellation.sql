\set ON_ERROR_STOP on

BEGIN;
CREATE TEMP TABLE manual_cancel_fixture (
    scenario text,
    participant text,
    email text,
    class_id bigint,
    enrollment_id bigint,
    seats integer,
    remaining_visits integer,
    valid_until date,
    starts_at timestamptz
) ON COMMIT DROP;

DO $$
DECLARE
    run_id text := gen_random_uuid()::text;
    test_hash text;
    test_room bigint;
    test_user bigint;
    test_class bigint;
    test_enrollment bigint;
    test_email text;
    test_start timestamptz;
    test_valid_until date;
    scenario_row record;
    participant_row record;
BEGIN
    SELECT password_hash INTO STRICT test_hash
    FROM users WHERE email = 'user001@club.test';
    SELECT id INTO STRICT test_room FROM rooms ORDER BY id LIMIT 1;

    FOR scenario_row IN
        SELECT * FROM (VALUES
            ('early',         interval '4 hours',    false, 5,  30),
            ('late',          interval '90 minutes', false, 5,  30),
            ('club-future',   interval '90 minutes', true,  5,  30),
            ('club-past',     interval '-30 minutes',true,  5,  30),
            ('started',       interval '-30 minutes',false, 5,  30),
            ('expired-empty', interval '4 hours',    false, 0,  -1)
        ) AS cases(code, starts_in, cancelled, visits, valid_days)
    LOOP
        test_email := 'manual-cancel-' || scenario_row.code || '-' || run_id || '@club.test';
        test_start := now() + scenario_row.starts_in;
        test_valid_until := (now() AT TIME ZONE 'UTC')::date + scenario_row.valid_days;
        INSERT INTO users (email, password_hash, role, created_at)
        VALUES (test_email, test_hash, 'user', now()) RETURNING id INTO test_user;
        INSERT INTO passes (user_id, remaining_visits, valid_until)
        VALUES (test_user, scenario_row.visits, test_valid_until);
        INSERT INTO classes (room_id, title, starts_at, duration_minutes, capacity, is_cancelled)
        VALUES (test_room, 'Manual cancel: ' || scenario_row.code, test_start, 60, 10, scenario_row.cancelled)
        RETURNING id INTO test_class;
        INSERT INTO enrollments (user_id, class_id, seats, status, created_at)
        VALUES (test_user, test_class, 2, 'active', now() - interval '1 day')
        RETURNING id INTO test_enrollment;
        INSERT INTO manual_cancel_fixture VALUES
            (scenario_row.code, 'owner', test_email, test_class, test_enrollment,
             2, scenario_row.visits, test_valid_until, test_start);
    END LOOP;

    FOR scenario_row IN
        SELECT * FROM (VALUES
            ('fifo', interval '3 days'), ('fifo-late', interval '90 minutes')
        ) AS cases(code, starts_in)
    LOOP
        test_start := now() + scenario_row.starts_in;
        test_valid_until := (now() AT TIME ZONE 'UTC')::date + 30;
        INSERT INTO classes (room_id, title, starts_at, duration_minutes, capacity, is_cancelled)
        VALUES (test_room, 'Manual cancel: ' || scenario_row.code, test_start, 60, 5, false)
        RETURNING id INTO test_class;

        FOR participant_row IN
            SELECT * FROM (VALUES ('owner', 3), ('A', 1), ('B', 2), ('C', 1)) AS people(name, seats)
        LOOP
            test_email := 'manual-cancel-' || scenario_row.code || '-' || lower(participant_row.name)
                || '-' || run_id || '@club.test';
            INSERT INTO users (email, password_hash, role, created_at)
            VALUES (test_email, test_hash, 'user', now()) RETURNING id INTO test_user;
            INSERT INTO passes (user_id, remaining_visits, valid_until)
            VALUES (test_user, 5, test_valid_until);
            test_enrollment := NULL;
            IF participant_row.name = 'owner' THEN
                INSERT INTO enrollments (user_id, class_id, seats, status, created_at)
                VALUES (test_user, test_class, 3, 'active', now() - interval '1 day')
                RETURNING id INTO test_enrollment;
            END IF;
            INSERT INTO manual_cancel_fixture VALUES
                (scenario_row.code, participant_row.name, test_email, test_class, test_enrollment,
                 participant_row.seats, 5, test_valid_until, test_start);
            IF participant_row.name = 'owner' THEN
                INSERT INTO enrollments (user_id, class_id, seats, status, created_at)
                VALUES (test_user, test_class, 2, 'active', now() - interval '1 day')
                RETURNING id INTO test_enrollment;
                INSERT INTO manual_cancel_fixture VALUES
                    (scenario_row.code, 'owner-2', test_email, test_class, test_enrollment,
                     2, 5, test_valid_until, test_start);
            END IF;
        END LOOP;
    END LOOP;
END $$;

SELECT scenario, participant, email, class_id, enrollment_id, seats,
       remaining_visits, valid_until,
       starts_at AT TIME ZONE 'UTC' AS starts_at_utc,
       starts_at AT TIME ZONE 'Europe/Minsk' AS starts_at_minsk
FROM manual_cancel_fixture
ORDER BY scenario, CASE participant WHEN 'owner' THEN 0 WHEN 'owner-2' THEN 1
    WHEN 'A' THEN 2 WHEN 'B' THEN 3 ELSE 4 END;
COMMIT;
