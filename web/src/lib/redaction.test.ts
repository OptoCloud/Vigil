import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { deviceLabel, REDACTION_MARKER, splitWithheld } from './redaction';

describe('the redaction marker', () => {
	it('is the one vigil-core writes', () => {
		const core = JSON.parse(readFileSync(new URL('../../../core/contract.json', import.meta.url), 'utf8')) as {
			constants: { redactionMarker: string };
		};
		expect(REDACTION_MARKER).toBe(core.constants.redactionMarker);
	});
});

describe('splitWithheld', () => {
	it('splits a shown tail from the marker in front of it', () => {
		expect(splitWithheld('[REDACTED]XK2P')).toEqual([
			{ text: '[REDACTED]', withheld: true },
			{ text: 'XK2P', withheld: false }
		]);
	});

	it('finds every marker in a message, and leaves text without one whole', () => {
		expect(splitWithheld('a_[REDACTED]0000 and b_[REDACTED]1111').map((part) => part.withheld)).toEqual([false, true, false, true, false]);
		expect(splitWithheld('Pool tank is DEGRADED.')).toEqual([{ text: 'Pool tank is DEGRADED.', withheld: false }]);
		expect(splitWithheld('[REDACTED]')).toEqual([{ text: '[REDACTED]', withheld: true }]);
	});
});

describe('deviceLabel', () => {
	it('drops the bus and the underscore udev joined the serial with', () => {
		expect(deviceLabel('ata-ST4000VN008-2DR166_[REDACTED]0000')).toBe('ST4000VN008-2DR166 [REDACTED]0000');
		expect(deviceLabel('nvme-Samsung_SSD_980_1TB_[REDACTED]AB12_1')).toBe('Samsung_SSD_980_1TB [REDACTED]AB12_1');
	});

	it('leaves names that withhold nothing alone', () => {
		for (const name of ['wwn-0x5000c500a0000000', 'raidz2-0', '/tmp/zt1', 'ata-ST4000VN008_ZA1B2C3D']) expect(deviceLabel(name)).toBe(name);
	});
});
