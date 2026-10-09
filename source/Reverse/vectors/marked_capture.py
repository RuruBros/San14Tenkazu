# Verify the real capture caller reaches marked removal and returns without ordinary prisoner writes.
from corps_commands import generate as collect
from marked_officer import fixtures as removal_fixtures

COVER = [0x2E0CF0, 0x264DD0, 0x32B5A0]


def fixtures(ctx, snapshot, source, destination):
    return [dict(f, unit=None, force=None, officer=None) for f in removal_fixtures(ctx, snapshot, source, destination)
            if f['name'] != 'marked_already_free']


def generate(ctx):
    return collect(ctx, fixtures, 0x2E0CF0, 'marked_capture', capture_free_lists=True)
