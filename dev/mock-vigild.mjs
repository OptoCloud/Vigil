// A stand-in for vigild, for developing vigil-core and the dashboard without the hardware
// (and on Windows, where the real daemon's unix socket isn't an option). Serves the same
// GET /status and GET /events over TCP, with plausibly wandering temperatures.
//
//   node dev/mock-vigild.mjs [port]            then run vigil-core with VIGILD_URL=http://127.0.0.1:5178
//
// Type a letter + Enter to inject a fault: s = stall a fan, g = GPU unreadable,
// u = loop unhealthy, r = grow a drive's reallocated count, z = fault a pool disk onto a spare
// (void's night of 2026-10-08), c = clear all faults.

import http from 'node:http';

const port = Number(process.argv[2] ?? 5178);
const POLL_MS = 2000;

const faults = { stall: false, gpu: false, unhealthy: false, pool: false };
let reallocated = 1048;
let driveHealthAsOf = new Date().toISOString();

// orion's ports: eight on the LSI's phys, three in the 4-bay stack (ata-4 is empty), and the
// two SSDs on ata-1 and ata-2.
const PORTS = [
	...Array.from({ length: 8 }, (_, phy) => `pci-0000:03:00.0-sas-phy${phy}-lun-0`),
	'pci-0000:01:00.1-ata-3',
	'pci-0000:01:00.1-ata-6',
	'pci-0000:01:00.1-ata-5',
	'pci-0000:01:00.1-ata-1',
	'pci-0000:01:00.1-ata-2'
];

const drives = Array.from({ length: 13 }, (_, i) => ({
	wwn: `naa.5000c500${(0xa0000000 + i * 0x1111111).toString(16)}`,
	device: `sd${String.fromCharCode(97 + i)}`,
	port: PORTS[i],
	base: 30 + ((i * 7) % 9),
	ssd: i >= 11
}));

const fans = [
	{ id: 'drive-cage', rpmPerPercent: 18.6 },
	{ id: 'cpu-cooler-front', rpmPerPercent: 17.2 },
	{ id: 'cpu-cooler-rear', rpmPerPercent: 17.6 },
	{ id: 'intake-cpu', rpmPerPercent: 16.4 },
	{ id: 'intake-gpu-lsi', rpmPerPercent: 12.7 },
	{ id: 'lsi-cooling', rpmPerPercent: 56 }
];

// A slow load cycle plus noise, so the charts have some shape to them.
const wave = (periodSeconds, phase = 0) => Math.sin((Date.now() / 1000 / periodSeconds) * 2 * Math.PI + phase);
const noise = (amount) => (Math.random() - 0.5) * amount;
const duty = (celsius, low, high) => Math.round(Math.min(100, Math.max(30, 30 + ((celsius - low) / (high - low)) * 70)));

const TIB = 2 ** 40;

// void: a raidz2 of the eight LSI disks with the 4-bay pair as hot spares; rpool: the SSD mirror.
// Under the pool fault, the sixth disk is FAULTED and the first spare stands in for it.
function zfs() {
	const leaf = (d, parent, changes = {}) => ({
		name: `ata-ST4000VN008_${d.wwn.slice(-6).toUpperCase()}`,
		guid: String(9000000000000000000n + BigInt(d.wwn.length * 1000 + drives.indexOf(d))),
		path: `/dev/disk/by-id/ata-ST4000VN008_${d.wwn.slice(-6).toUpperCase()}-part1`,
		deviceName: d.wwn,
		port: d.port,
		parent,
		class: 'normal',
		state: 'ONLINE',
		aux: null,
		readErrors: 0,
		writeErrors: 0,
		checksumErrors: 0,
		slowIos: 0,
		...changes
	});
	const idle = { parent: null, class: 'spare', state: 'AVAIL', readErrors: null, writeErrors: null, checksumErrors: null, slowIos: null };
	const members = drives.slice(0, 8).map((d, i) =>
		faults.pool && i === 5 ? leaf(d, 'spare-5', { state: 'FAULTED', aux: 'ERR_EXCEEDED', readErrors: 14 }) : leaf(d, 'raidz2-0')
	);
	const spares = faults.pool
		? [leaf(drives[8], 'spare-5', { class: 'spare', state: 'INUSE' }), leaf(drives[9], null, idle)]
		: [leaf(drives[8], null, idle), leaf(drives[9], null, idle)];
	const started = Math.floor(Date.now() / 1000) - 3 * 3600;
	return {
		isAvailable: true,
		asOf: new Date().toISOString(),
		pools: [
			{
				name: 'void',
				state: faults.pool ? 'DEGRADED' : 'ONLINE',
				errorCount: 0,
				sizeBytes: Math.round(29.1 * TIB),
				allocatedBytes: Math.round(11.8 * TIB),
				scan: faults.pool
					? { function: 'RESILVER', state: 'SCANNING', startTime: new Date(started * 1000).toISOString(), endTime: null, toExamineBytes: Math.round(11.8 * TIB), examinedBytes: Math.round(7.1 * TIB), issuedBytes: Math.round(4.2 * TIB), processedBytes: Math.round(0.5 * TIB), errors: 0 }
					: { function: 'SCRUB', state: 'FINISHED', startTime: '2026-10-11T00:24:01.000Z', endTime: '2026-10-11T09:10:54.000Z', toExamineBytes: Math.round(11.8 * TIB), examinedBytes: Math.round(11.8 * TIB), issuedBytes: Math.round(11.8 * TIB), processedBytes: 0, errors: 0 },
				vdevs: [...members, ...spares]
			},
			{
				name: 'rpool',
				state: 'ONLINE',
				errorCount: 0,
				sizeBytes: Math.round(0.45 * TIB),
				allocatedBytes: Math.round(0.12 * TIB),
				scan: null,
				vdevs: drives.slice(11).map((d) => leaf(d, 'mirror-0'))
			}
		]
	};
}

function snapshot() {
	const cpu = 46 + wave(600) * 12 + noise(1.5);
	const gpu = 40 + wave(900, 1) * 6 + noise(1);
	const hba = 54 + wave(1200, 2) * 3 + noise(0.4);
	const driveTemps = drives.map((d) => Math.round(d.base + wave(1800, d.base) * 2 + noise(0.6)));
	const hottestDrive = Math.max(...driveTemps);

	const reading = (id, category, label, celsius) => ({
		id,
		category,
		label,
		celsiusOrNull: celsius === null ? null : Math.round(celsius * 100) / 100,
		sourcePath: 'mock',
		isAvailable: celsius !== null
	});

	const duties = {
		'drive-cage': duty(hottestDrive, 28, 48),
		'cpu-cooler-front': duty(cpu, 30, 75),
		'cpu-cooler-rear': duty(cpu, 30, 75),
		'intake-cpu': duty(cpu, 30, 80),
		'intake-gpu-lsi': faults.gpu ? 80 : duty(Math.max(gpu, hba), 40, 85),
		'lsi-cooling': duty(Math.max(hba, hottestDrive), 45, 75)
	};

	return {
		timestampUtc: new Date().toISOString(),
		sensors: [
			reading('cpu', 'cpu', 'Tctl', cpu),
			reading('board', 'boardAmbient', 'SYSTIN', 33 + wave(2400) + noise(0.3)),
			...drives.map((d, i) => ({ ...reading(`drive:${d.wwn}`, 'drive', d.wwn, driveTemps[i]), port: d.port })),
			reading('dimm:hwmon15', 'memory', 'hwmon15', 32 + wave(1500) * 1.5),
			reading('dimm:hwmon16', 'memory', 'hwmon16', 33 + wave(1500, 1) * 1.5),
			reading('gpu', 'gpu', 'GPU', faults.gpu ? null : gpu),
			reading('hba', 'hba', 'IOC Temperature', hba)
		],
		fans: fans.map((fan) => {
			const stalled = faults.stall && fan.id === 'drive-cage';
			return {
				id: fan.id,
				dutyPercent: duties[fan.id],
				rpm: stalled ? 0 : Math.round(duties[fan.id] * fan.rpmPerPercent + noise(20)),
				mode: 'manual',
				stalled
			};
		}),
		driveHealth: drives.map((d, i) => ({
			deviceName: d.wwn,
			port: d.port,
			passed: true,
			reallocatedSectorCount: i === 2 ? reallocated : 0,
			pendingSectorCount: d.ssd ? null : 0,
			powerOnHours: 9000 + i * 1300,
			sourcePath: `/dev/${d.device}`,
			isAvailable: true,
			asOf: driveHealthAsOf
		})),
		zfs: zfs(),
		controlLoopHealthy: !faults.unhealthy
	};
}

const clients = new Set();
setInterval(() => {
	const event = `event: status\ndata: ${JSON.stringify(snapshot())}\n\n`;
	for (const client of clients) client.write(event);
}, POLL_MS);

http
	.createServer((request, response) => {
		if (request.url === '/status') {
			response.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify(snapshot()));
		} else if (request.url === '/events') {
			response.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache' });
			response.write(`event: status\ndata: ${JSON.stringify(snapshot())}\n\n`);
			clients.add(response);
			request.on('close', () => clients.delete(response));
		} else {
			response.writeHead(404).end();
		}
	})
	.listen(port, '127.0.0.1', () => console.log(`mock vigild on http://127.0.0.1:${port} (s/g/u/r/z/c + Enter to inject faults)`));

process.stdin.on('data', (input) => {
	const key = input.toString().trim();
	if (key === 's') faults.stall = true;
	else if (key === 'g') faults.gpu = true;
	else if (key === 'u') faults.unhealthy = true;
	else if (key === 'r') {
		reallocated += 8;
		driveHealthAsOf = new Date().toISOString();
	} else if (key === 'z') faults.pool = true;
	else if (key === 'c') faults.stall = faults.gpu = faults.unhealthy = faults.pool = false;
	console.log('faults:', faults, 'reallocated:', reallocated);
});
