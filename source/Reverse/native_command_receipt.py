"""Verify the ordered live command suite receipts without attaching to the game."""
import argparse
import json
from pathlib import Path

from runtime_roundtrip_receipt import require


COMMANDS = ['Camera', 'Calendar', 'Item', 'Alliance', 'Give', 'Capture', 'Transfer',
            'Revival', 'Independence', 'Surrender', 'Demo']


def by_id(state, collection, key, value):
    rows = [row for row in state[collection] if row[key] == value]
    require(len(rows) == 1, f'{collection} target {value} missing or ambiguous')
    return rows[0]


def owner(state, base):
    collection, key = ('cities', 'id') if base <= 51 else ('gates', 'Id')
    return by_id(state, collection, key, base)['owner']


def verify_command(before, after):
    command = before['command']
    a, b, target = before['state'], after['state'], before['target']
    require(a['session'] == b['session'] and a['Hour'] == b['Hour'], 'Session or clock moved')
    require(command == 'Calendar' or a['Date'] == b['Date'], 'Unexpected date change')
    require(command == 'Demo' or a['PlayerForce'] == b['PlayerForce'], 'Unexpected player change')
    if command == 'Camera':
        old, new = before['camera'], after['camera']
        def arrived(sample):
            return abs(sample['X'] - sample['TargetX']) <= 32 and abs(sample['Z'] - sample['TargetZ']) <= 32
        require(not arrived(old) and arrived(new) and
                [old['TargetX'], old['TargetZ']] == [new['TargetX'], new['TargetZ']], 'Camera target unconfirmed')
    elif command == 'Calendar':
        require(a['Date'] != b['Date'] and [b['Date'][f'Item{i}'] for i in (1, 2, 3)] ==
                [target[key] for key in ('Year', 'Month', 'Day')], 'Calendar date unconfirmed')
    elif command in ('Item', 'Alliance'):
        collection, key, count = ('items', 'Index', 1) if command == 'Item' else ('forces', 'Id', 2)
        old = {row[key]: row['hash'] for row in a[collection]}
        new = {row[key]: row['hash'] for row in b[collection]}
        require(old.keys() == new.keys() and sum(new[k] != value for k, value in old.items()) == count,
                f'{command} changed an unexpected number of records')
    elif command == 'Give':
        require(owner(a, target['baseId']) == 0 and owner(b, target['baseId']) == target['force'],
                'Base owner unconfirmed')
    elif command in ('Capture', 'Transfer', 'Revival'):
        officer = by_id(b, 'officers', 'Id', target.get('Officer', target.get('Id')))
        if command == 'Capture':
            require(officer['Status'] == 6 and owner(b, officer['BaseId']) == target['Force'], 'Captivity unconfirmed')
        elif command == 'Transfer':
            require(officer['BaseId'] == target['target'], 'Transfer destination unconfirmed')
        else:
            require(officer['BaseId'] == target['Base'] and officer['Status'] == (4 if target['Mode'] == 0 else 5),
                    'Revival status or base unconfirmed')
    elif command == 'Independence':
        require(owner(b, target['City']) == target['NewForce'] and
                by_id(b, 'forces', 'Id', target['NewForce'])['Ruler'] == target['Governor'], 'Independence unconfirmed')
    elif command == 'Surrender':
        force = [row for row in b['forces'] if row['Id'] == target['From']]
        require(not force or force[0]['Ruler'] != target['FromRuler'], 'Surrendering ruler still in power')
        require(by_id(b, 'forces', 'Id', target['To'])['Ruler'] == target['ToRuler'], 'Receiving ruler changed')
    elif command == 'Demo':
        require(a['IsDemoPlay'] and not b['IsDemoPlay'] and b['PlayerForce'] == target['force'], 'Demo exit unconfirmed')


def verify(rows):
    start = rows[0]
    require(start['stage'] == 'started' and start['commands'] == COMMANDS and start['ProcessId'] > 0, 'Wrong suite header')
    completed, failed, pending, done = [], [], None, False
    for row in rows[1:]:
        stage = row['stage']
        require(not done, 'Entries after final receipt')
        if stage == 'all-confirmed':
            require(completed == COMMANDS and row['passed'] == len(COMMANDS) and pending is None, 'Invalid final receipt')
            done = True
            continue
        require(len(completed) < len(COMMANDS) and row['command'] == COMMANDS[len(completed)], 'Command out of order')
        if stage == 'before':
            require(pending is None, 'Two overlapping attempts')
            pending = row
        elif stage == 'after':
            require(pending is not None, 'Result without baseline')
            verify_command(pending, row)
            completed.append(row['command'])
            pending = None
        elif stage == 'failed':
            require(pending is not None, 'Failure without baseline')
            failed.append({'command': row['command'], 'error': row['error']})
            pending = None
        else:
            require(stage in ('armed', 'disarmed') and pending is None, 'Unexpected suite event')
    return dict(pid=start['ProcessId'], completed=completed, failed=failed, remaining=COMMANDS[len(completed):],
                inFlight=pending is not None, allConfirmed=done)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('log', type=Path)
    args = parser.parse_args()
    print(json.dumps(verify([json.loads(line) for line in args.log.read_text(encoding='utf-8-sig').splitlines()]),
                     ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
