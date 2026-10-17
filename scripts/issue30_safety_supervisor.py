#!/usr/bin/env python3
"""Issue30 emergency resource-stop supervisor; not a graceful Worker drain.
Default plan only; never activates/restores. Search pilots must begin with Worker
already stopped/quiescent and keep it stopped. Never wait for leases on emergency
parent exit or deadline: preserve uncertain attempts/leases for operator recovery.
Frozen legacy source is data in validation_safety/provenance.json; its CLI entry points are disabled.
A sleeping/offline host or SIGKILL requires a separate-host stop operator.
"""
import argparse
import json
import math
import os
import select
import signal
import subprocess
import sys
import time

from azure_validation import RG, environment_id, matches_environment, revision_inventory, rows

SUB = 'e17a163e-1f3f-4a5d-8709-b8697066a8e5'
ENV = environment_id(SUB, 'dse-environment-vnet-jpe')
ALLOWED = ('dse-api-vnet', 'dse-worker-vnet')


class SafetyError(Exception):
    def __init__(self, code):
        self.code = code
        super().__init__(code)


class Deadline:
    def __init__(self, seconds, clock=time.monotonic):
        self.clock = clock
        self.end = clock() + seconds

    @classmethod
    def at(cls, end, clock=time.monotonic):
        result = cls(0, clock)
        result.end = end
        return result

    def remaining(self):
        remaining = self.end - self.clock()
        if not math.isfinite(remaining) or remaining <= 0:
            raise SafetyError('DEADLINE_EXCEEDED')
        return remaining


class AzureCli:
    def __init__(self, command, call_timeout=8.0):
        self.command = list(command)
        self.call_timeout = call_timeout

    def call(self, parts, deadline):
        deadline.remaining()
        command = [*self.command, *parts, '--subscription', SUB, '--only-show-errors', '--output', 'json']
        process = None
        try:
            process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                       text=True, start_new_session=True)
            output, _ = process.communicate(timeout=min(self.call_timeout, deadline.remaining()))
            deadline.remaining()
            if process.returncode:
                raise SafetyError('CLI_REJECTED')
            # Azure CLI may emit no document for a successful stop mutation.
            # Reads still require valid JSON; only deactivation accepts silence.
            if isinstance(output, str) and not output.strip() and tuple(parts[:3]) == ('containerapp', 'revision', 'deactivate'):
                return None
            def invalid_constant(_): raise ValueError('Nonfinite JSON')
            return json.loads(output, parse_constant=invalid_constant)
        except subprocess.TimeoutExpired:
            raise SafetyError('CLI_TIMEOUT') from None
        except OSError:
            raise SafetyError('CLI_UNAVAILABLE') from None
        except (ValueError, TypeError):
            raise SafetyError('INVALID_JSON') from None
        finally:
            if process is not None:
                # No unbounded communicate/wait after the shared deadline. Kill the
                # dedicated CLI process group, including extension child processes,
                # even when the CLI exited successfully and its children closed pipes.
                try: os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError: pass
                except OSError: raise SafetyError('CLI_CHILD_STOP_UNCONFIRMED') from None
                try: process.wait(timeout=.05)
                except subprocess.TimeoutExpired: pass
                for pipe in (process.stdout, process.stderr):
                    if pipe: pipe.close()


class ParentWatch:
    """Watch an immutable OS process handle; never confuse PID reuse with liveness."""
    def __init__(self, pid, wake_fd=None):
        self.wake_fd = wake_fd
        self.handle = None
        self.exited = False
        try:
            if hasattr(select, 'kqueue'):
                self.handle = select.kqueue()
                event = select.kevent(pid, filter=select.KQ_FILTER_PROC,
                    flags=select.KQ_EV_ADD | select.KQ_EV_ONESHOT, fflags=select.KQ_NOTE_EXIT)
                events = [event]
                if wake_fd is not None:
                    events.append(select.kevent(wake_fd, filter=select.KQ_FILTER_READ, flags=select.KQ_EV_ADD))
                self.handle.control(events, 0, 0)
                self.kind = 'kqueue'
            elif hasattr(os, 'pidfd_open'):
                self.handle = os.pidfd_open(pid)
                self.kind = 'pidfd'
            else:
                raise SafetyError('PARENT_WATCH_UNAVAILABLE')
        except ProcessLookupError:
            if self.handle is not None and hasattr(self.handle, 'close'): self.handle.close()
            self.handle = None
            self.exited = True
        except OSError:
            self.close()
            raise SafetyError('PARENT_WATCH_UNAVAILABLE') from None

    def wait(self, timeout):
        if self.exited: return True
        try:
            if self.kind == 'kqueue':
                events = self.handle.control(None, 2, timeout)
                parent_exited = any(event.filter == select.KQ_FILTER_PROC for event in events)
                wake_ready = any(event.filter == select.KQ_FILTER_READ for event in events)
            else:
                ready = select.select([self.handle] + ([self.wake_fd] if self.wake_fd is not None else []), [], [], timeout)[0]
                parent_exited = self.handle in ready
                wake_ready = self.wake_fd is not None and self.wake_fd in ready
            if wake_ready:
                try: os.read(self.wake_fd, 4096)
                except BlockingIOError: pass
            return parent_exited
        except OSError:
            raise SafetyError('PARENT_WATCH_FAILED') from None

    def close(self):
        if self.handle is not None:
            if hasattr(self.handle, 'close'): self.handle.close()
            else: os.close(self.handle)
            self.handle = None


def inventory(cli, app, deadline):
    value = cli.call(['containerapp', 'revision', 'list', '--all', '--name', app, '--resource-group', RG,
        '--query', '[].{name:name,active:properties.active,replicas:properties.replicas,status:properties.runningState}'], deadline)
    try: result = revision_inventory(value)
    except (ValueError, TypeError): raise SafetyError('INVALID_REVISION_INVENTORY') from None
    if not result: raise SafetyError('MISSING_REVISION_INVENTORY')
    return result


def prepare_stop(cli, app, deadline):
    if app not in ALLOWED: raise SafetyError('TARGET_REFUSED')
    identity = cli.call(['containerapp', 'show', '--name', app, '--resource-group', RG, '--query',
        '{name:name,environment:properties.environmentId,managedEnvironment:properties.managedEnvironmentId}'], deadline)
    if not isinstance(identity, dict) or not matches_environment(identity, app, ENV):
        raise SafetyError('ENVIRONMENT_REFUSED')
    return [row['name'] for row in inventory(cli, app, deadline) if row['active']]


def request_stop(cli, app, revision, deadline):
    deadline.remaining()
    cli.call(['containerapp', 'revision', 'deactivate', '--name', app, '--resource-group', RG,
              '--revision', revision], deadline)


def confirm_stop(cli, app, deadline):
    revisions = inventory(cli, app, deadline)
    if any(r['active'] or r['replicas'] != 0 or r['status'] != 'Stopped' for r in revisions): return False
    for row in revisions:
        deadline.remaining()
        actual = cli.call(['containerapp', 'replica', 'list', '--name', app, '--resource-group', RG,
                          '--revision', row['name'], '--query', '[].{name:name}'], deadline)
        try: actual = rows(actual)
        except ValueError: raise SafetyError('INVALID_REPLICA_INVENTORY') from None
        if actual: return False
    # A revision added/changed while actual replica lists were being read must not
    # disappear from the snapshot. This is still not an atomic future-stop guarantee.
    final = inventory(cli, app, deadline)
    return (set(r['name'] for r in final) == set(r['name'] for r in revisions)
            and all(not r['active'] and r['replicas'] == 0 and r['status'] == 'Stopped' for r in final))


def stop_app(cli, app, deadline):
    for revision in prepare_stop(cli, app, deadline):
        request_stop(cli, app, revision, deadline)
    return confirm_stop(cli, app, deadline)


def stop_apps(cli, apps, deadline, poll_seconds):
    pending, confirmed, failures = list(apps), [], []
    try:
        while pending:
            deadline.remaining()
            requests = {}
            preparing = pending[:]
            for index, app in enumerate(preparing):
                # Each app keeps its own share of the common deadline. Reserve
                # one third for its first request; slow preparation must neither
                # consume that slot nor the next app's validation/request window.
                now = deadline.clock()
                share = deadline.remaining() / (len(preparing) - index)
                app_budget = Deadline.at(min(deadline.end, now + share), deadline.clock)
                preparation_budget = Deadline.at(now + share * 2 / 3, deadline.clock)
                try:
                    requests[app] = prepare_stop(cli, app, preparation_budget)
                    if requests[app]:
                        request_stop(cli, app, requests[app].pop(0), app_budget)
                except SafetyError as error:
                    failures.append({'app': app, 'code': error.code})
                    # Invalid/denied reads are never reclassified as an empty list.
                    # Continue to the other app within the same remaining budget.
                    pending.remove(app)
                    requests.pop(app, None)
            # Request one revision per app each round before spending any budget
            # on confirmation. Many API revisions must not starve Worker requests.
            while any(requests.values()):
                requesting = [app for app in pending if requests.get(app)]
                for index, app in enumerate(requesting):
                    try:
                        now = deadline.clock()
                        share = deadline.remaining() / (len(requesting) - index)
                        request_budget = Deadline.at(min(deadline.end, now + share), deadline.clock)
                        request_stop(cli, app, requests[app].pop(0), request_budget)
                    except SafetyError as error:
                        failures.append({'app': app, 'code': error.code})
                        pending.remove(app); requests.pop(app, None)
            for app in pending[:]:
                try:
                    if confirm_stop(cli, app, deadline):
                        confirmed.append(app); pending.remove(app)
                except SafetyError as error:
                    failures.append({'app': app, 'code': error.code})
                    pending.remove(app)
            if pending: time.sleep(min(poll_seconds, deadline.remaining()))
    except SafetyError as error:
        failures.append({'code': error.code})
    resource_stopped = len(confirmed) == len(apps)
    return {'stopConfirmed': resource_stopped, 'resourceStopConfirmed': resource_stopped,
            'shutdownMode': 'emergency-resource-stop', 'workerQuiescenceConfirmed': False,
            'gracefulStopConfirmed': False, 'inFlightRecoveryRequired': 'dse-worker-vnet' in apps,
            'confirmedApps': confirmed,
            'unconfirmedApps': [app for app in apps if app not in confirmed], 'failures': failures,
            'keepStoppedRequired': True, 'restorationConfirmed': False, 'liveAcceptance': 'pending'}


def emit(value):
    print(json.dumps(value, allow_nan=False), flush=True)


def mute_failed_stdout():
    # Prevent a second broken-pipe flush during interpreter shutdown. Reporting
    # must never replace or prevent the emergency resource-stop path.
    try:
        descriptor = sys.stdout.fileno()
        sink = os.open(os.devnull, os.O_WRONLY)
        try: os.dup2(sink, descriptor)
        finally: os.close(sink)
    except (OSError, ValueError):
        pass


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--execute-after-approval', action='store_true')
    parser.add_argument('--parent-pid', type=int)
    parser.add_argument('--apps', nargs='+', choices=ALLOWED, default=list(ALLOWED))
    parser.add_argument('--run-seconds', type=float)
    parser.add_argument('--stop-seconds', type=float, default=75)
    parser.add_argument('--call-timeout-seconds', type=float, default=8)
    parser.add_argument('--poll-seconds', type=float, default=1)
    parser.add_argument('--cli-command', nargs='+', default=['az'])
    args = parser.parse_args()
    for name in ('run_seconds', 'stop_seconds', 'call_timeout_seconds', 'poll_seconds'):
        value = getattr(args, name)
        if value is not None and (not math.isfinite(value) or value < 0 or (name != 'run_seconds' and value == 0)):
            parser.error('Finite duration required; stop/call/poll durations must be positive')
    if args.run_seconds is not None and not math.isfinite(args.run_seconds + args.stop_seconds):
        parser.error('Finite total duration required')
    apps = list(dict.fromkeys(args.apps))
    if not args.execute_after_approval:
        emit({'mode': 'PLAN_ONLY', 'apps': apps, 'runSeconds': args.run_seconds,
              'stopSeconds': args.stop_seconds, 'approvalGranted': False})
        return 0
    if args.parent_pid is None or args.parent_pid <= 1 or args.parent_pid == os.getpid() or args.run_seconds is None:
        parser.error('Explicit distinct parent PID and run-seconds required')
    if set(apps) != set(ALLOWED):
        parser.error('Both new API and Worker must be included in the stop scope')
    started = time.monotonic()
    total = Deadline.at(started + args.run_seconds + args.stop_seconds)
    run_end = started + args.run_seconds
    requested_at = None
    def request_stop(_signal, _frame):
        nonlocal requested_at
        # Repeated signals must not move the first stop deadline forward.
        if requested_at is None:
            requested_at = time.monotonic()
    handlers = {s: signal.getsignal(s) for s in (signal.SIGTERM, signal.SIGINT)}
    for s in handlers: signal.signal(s, request_stop)
    watcher = None
    wake_read = wake_write = previous_wakeup = None
    trigger = 'RUN_DEADLINE'
    reporting_failed = False
    try:
        try:
            # Python retries interrupted select/kqueue calls (PEP 475). A wakeup
            # descriptor makes the first signal observable without waiting for poll.
            wake_read, wake_write = os.pipe()
            os.set_blocking(wake_read, False)
            os.set_blocking(wake_write, False)
            previous_wakeup = signal.set_wakeup_fd(wake_write, warn_on_full_buffer=False)
        except (OSError, ValueError):
            raise SafetyError('SIGNAL_WAKEUP_UNAVAILABLE') from None
        watcher = ParentWatch(args.parent_pid, wake_read)
        emit({'event': 'SUPERVISOR_ARMED', 'parentPid': args.parent_pid, 'apps': apps,
              'runSeconds': args.run_seconds, 'stopSeconds': args.stop_seconds})
        while True:
            if requested_at is not None:
                trigger = 'SIGNAL'; break
            remaining = run_end - time.monotonic()
            if remaining <= 0: break
            if watcher.wait(min(args.poll_seconds, remaining)):
                trigger = 'PARENT_EXIT'; break
    except SafetyError as error:
        trigger = error.code
    except (OSError, ValueError):
        trigger = 'REPORT_SINK_FAILED'
        reporting_failed = True
        mute_failed_stdout()
    finally:
        if watcher:
            try: watcher.close()
            except OSError: trigger = 'PARENT_WATCH_CLEANUP_FAILED'
    # One fixed stop deadline shared by every app, revision and CLI call.
    # Early parent exit/signal does not spend the unused run window on retries.
    stopped_at = time.monotonic()
    stop_deadline = Deadline.at(min(total.end, stopped_at + args.stop_seconds,
        (requested_at + args.stop_seconds) if requested_at is not None else total.end))
    try:
        result = stop_apps(AzureCli(args.cli_command, args.call_timeout_seconds), apps, stop_deadline, args.poll_seconds)
        result.update(event='SUPERVISOR_FINISHED', trigger=trigger, elapsedSeconds=time.monotonic()-started,
                      totalBudgetSeconds=args.run_seconds+args.stop_seconds)
        try:
            emit(result)
        except (OSError, ValueError):
            reporting_failed = True
            mute_failed_stdout()
    finally:
        if previous_wakeup is not None:
            try: signal.set_wakeup_fd(previous_wakeup)
            except (OSError, ValueError): reporting_failed = True
        for descriptor in (wake_read, wake_write):
            if descriptor is not None:
                try: os.close(descriptor)
                except OSError: reporting_failed = True
        for s, handler in handlers.items(): signal.signal(s, handler)
    return 0 if result['stopConfirmed'] and not reporting_failed else 2


if __name__ == '__main__':
    raise SystemExit(main())
