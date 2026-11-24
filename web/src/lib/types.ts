// Mirrors two definitions of the JSON: vigild's snapshot (Rust, daemon/) and vigil-core's live
// stream (C#, core/). Each writes its half to a contract.json that types.contract.test.ts checks
// this file against, so a change on either side fails here until this file follows.
//
// Over the 400-line ceiling (STYLE.md §2.1): it is one mirror of two wire contracts, and
// splitting it would put the halves of one contract test's subject in separate files.

export type SensorCategory = 'cpu' | 'boardAmbient' | 'drive' | 'gpu' | 'memory' | 'hba';

export interface SensorReading {
	id: string;
	category: SensorCategory;
	label: string;
	celsiusOrNull: number | null;
	sourcePath: string;
	isAvailable: boolean;
	/**
	 * Drives only: the /dev/disk/by-path name of the port (bay) the drive is plugged into, e.g.
	 * "pci-0000:01:00.1-ata-3". The id says which disk; this says where it sits. Absent from
	 * daemons that predate ports.
	 */
	port?: string | null;
}

export type PwmMode = 'disabled' | 'manual' | 'thermalCruise' | 'speedCruise' | 'smartFanIII' | 'smartFanIV';

export interface FanStatus {
	id: string;
	dutyPercent: number;
	rpm: number | null;
	mode: PwmMode | null;
	stalled: boolean;
}

export interface DriveHealth {
	deviceName: string;
	port?: string | null;
	passed: boolean | null;
	reallocatedSectorCount: number | null;
	pendingSectorCount: number | null;
	powerOnHours: number | null;
	/** The serial on the drive's label: what to read off a disk before pulling it. */
	serialNumber: string | null;
	/** ATA 187, Reported_Uncorrect: reads the drive's own ECC could not correct. */
	reportedUncorrect: number | null;
	/** ATA 188, Command_Timeout. Packed counters on Seagate, so only its growth means anything. */
	commandTimeout: number | null;
	/** ATA 199, UDMA_CRC_Error_Count: link errors, usually the cable or the backplane. */
	udmaCrcErrorCount: number | null;
	/** Whether the newest completed self-test passed; null when none is logged. */
	lastSelfTestPassed: boolean | null;
	/** Power-on hours when it ran (some drives wrap this at 16 bits). */
	lastSelfTestHours: number | null;
	sourcePath: string;
	isAvailable: boolean;
	asOf: string;
}

/** One drive bay from vigild's `[[bays]]`: the port behind it and where it is in the case. */
export interface Bay {
	port: string;
	cage: string;
	/** Counted from 1, the way the cage is labelled. */
	slot: number;
}

export interface Snapshot {
	timestampUtc: string;
	sensors: SensorReading[];
	fans: FanStatus[];
	driveHealth: DriveHealth[];
	/** Null when vigild's [zfs] is off or has not polled yet; absent from daemons that predate it. */
	zfs?: ZfsStatus | null;
	/** Every configured bay, in config order; absent from a daemon that predates them. */
	bays?: Bay[];
	/** The host's /proc counters (ADR-025). Null when vigild's [host] is off; absent from daemons that predate it. The page reads `host` messages instead, which carry them as rates. */
	host?: HostStatus | null;
	controlLoopHealthy: boolean;
}

// ---- The host's load as vigild counts it (ADR-025): cumulative counters, never rates ----

export interface HostStatus {
	asOf: string;
	uptimeSeconds: number | null;
	memory: MemoryStatus | null;
	cpu: CpuTicks | null;
	load: LoadAverage | null;
	pressure: Pressure | null;
	disks: DiskCounters[];
	interfaces: InterfaceCounters[];
}

export interface MemoryStatus {
	totalBytes: number;
	availableBytes: number;
	freeBytes: number;
	buffersBytes: number;
	cachedBytes: number;
	swapTotalBytes: number;
	swapFreeBytes: number;
	arcBytes: number | null;
	arcMaxBytes: number | null;
}

export interface CpuTicks {
	user: number;
	nice: number;
	system: number;
	idle: number;
	iowait: number;
	irq: number;
	softirq: number;
	steal: number;
	logicalCpus: number;
}

export interface LoadAverage {
	one: number;
	five: number;
	fifteen: number;
}

export interface Pressure {
	cpu: PressureStall | null;
	memory: PressureStall | null;
	io: PressureStall | null;
}

export interface PressureStall {
	someMicros: number;
	fullMicros: number | null;
}

export interface DiskCounters {
	deviceName: string;
	port: string | null;
	readBytes: number;
	writtenBytes: number;
	busyMillis: number;
}

export interface InterfaceCounters {
	name: string;
	receivedBytes: number;
	transmittedBytes: number;
}

// ---- ZFS pool state, from one `zpool status` poll (ADR-018) ----
// States are zpool's own words, as strings: a state a later ZFS adds must read as "not ONLINE".

export interface ZfsStatus {
	/** False when zpool gave no usable answer. `pools` is then empty, meaning unknown, never "no pools". */
	isAvailable: boolean;
	asOf: string;
	pools: PoolStatus[];
}

export interface PoolStatus {
	name: string;
	/** ONLINE, DEGRADED, FAULTED, OFFLINE, REMOVED, UNAVAIL, SUSPENDED, or UNKNOWN. */
	state: string;
	/** Permanent data errors: blocks ZFS could not repair. */
	errorCount: number | null;
	/** Raw space across every disk, parity included, as `zpool list` counts it. */
	sizeBytes: number | null;
	allocatedBytes: number | null;
	/** The latest scrub or resilver. Null when the pool was never scanned. */
	scan: PoolScan | null;
	/** Every leaf vdev, hot spares included. */
	vdevs: VdevStatus[];
}

export interface PoolScan {
	/** SCRUB, RESILVER or ERRORSCRUB. */
	function: string;
	/** SCANNING, FINISHED or CANCELED. */
	state: string;
	startTime: string | null;
	/** Null while the scan is running. */
	endTime: string | null;
	toExamineBytes: number | null;
	examinedBytes: number | null;
	/** What zpool's own progress figure counts. */
	issuedBytes: number | null;
	processedBytes: number | null;
	errors: number | null;
}

export interface VdevStatus {
	name: string;
	/** A string: the u64 outgrows the numbers JavaScript holds exactly. */
	guid: string | null;
	path: string | null;
	/** The disk it is on, as DriveHealth.deviceName names it. Null for a vdev not on a disk. */
	deviceName: string | null;
	port: string | null;
	/** The vdev it sits in ("raidz2-0", "spare-5"), or null at the top of the pool. */
	parent: string | null;
	/** normal, special, dedup, log, l2cache or spare. */
	class: string;
	/** ONLINE, DEGRADED, FAULTED, OFFLINE, REMOVED, UNAVAIL; for a hot spare, AVAIL or INUSE. */
	state: string;
	/** Why it is not ONLINE, in zpool's words, e.g. ERR_EXCEEDED. */
	aux: string | null;
	readErrors: number | null;
	writeErrors: number | null;
	checksumErrors: number | null;
	slowIos: number | null;
}

// ---- The UPS, read from NUT's upsd (not from the daemon) ----

/**
 * One poll of a UPS's variables (`LIST VAR <ups>`). The named fields are the handful the
 * dashboard charts and alerts on; `variables` is everything upsd reported, verbatim.
 * Any of them is null when this UPS or driver doesn't provide it.
 */
export interface UpsReading {
	timestampUtc: string;
	/** The UPS's name on upsd, e.g. "apc". */
	name: string;
	model: string | null;
	/** ups.status split into its flags: OL, OB, LB, HB, RB, CHRG, DISCHRG, BYPASS, CAL, OFF, OVER, TRIM, BOOST, FSD. */
	status: string[];
	/** Percent. */
	batteryCharge: number | null;
	batteryRuntimeSeconds: number | null;
	/** Percent of the UPS's capacity. */
	load: number | null;
	/** Watts, derived from load and ups.realpower.nominal when the UPS doesn't report it directly. */
	realPower: number | null;
	inputVoltage: number | null;
	outputVoltage: number | null;
	batteryVoltage: number | null;
	/** °C. Heat is what ages a lead-acid battery fastest. */
	batteryTemperature: number | null;
	/** battery.date: when the battery was installed, as the UPS was told. Formatted as the driver pleases. */
	batteryDate: string | null;
	/** battery.runtime.low: below this much runtime the UPS is LB and upsmon starts the shutdown. */
	lowBatteryRuntimeSeconds: number | null;
	/** battery.charge.low, percent: the other way to LB, whichever comes first. */
	lowBatteryCharge: number | null;
	/** input.transfer.reason: why the UPS last went to battery, e.g. "LowInputVoltage". */
	transferReason: string | null;
	outputFrequency: number | null;
	/** Amps. */
	outputCurrent: number | null;
	/** How many monitors (upsmon) are logged in to upsd. 0 means nothing will shut the host down; null, upsd wouldn't say. */
	monitors: number | null;
	variables: Record<string, string>;
}

/** The operator's numbers the UPS is judged against, where the reading can't say. The shutdown thresholds come from upsd. */
export interface UpsLimits {
	/** How long the host takes to shut down (HOST_SHUTDOWN_SECONDS). */
	hostShutdownSeconds: number | null;
	/** °C at which the battery counts as too hot. */
	batteryTemperatureWarn: number;
}

export interface UpsState {
	/** False when NUT isn't configured (no NUT_HOST): the UPS section is hidden, not shown as broken. */
	enabled: boolean;
	/** The UPS's name on upsd, which its history is stored under. */
	name: string;
	/** The latest reading, or null while upsd can't be reached or has no fresh data for the UPS. */
	reading: UpsReading | null;
	/** Why there is no reading: a connection error, or upsd's own (DATA-STALE, DRIVER-NOT-CONNECTED, UNKNOWN-UPS). */
	error: string | null;
	limits: UpsLimits;
}

// ---- What the dashboard adds on top ----

/** A drive's last GOOD health result. The daemon only reports what its latest poll saw, and a sleeping drive isn't woken, so this is what survives those gaps. */
export interface DriveState {
	wwn: string;
	/** The bay it was in when it last answered. */
	port: string | null;
	passed: boolean | null;
	reallocatedSectorCount: number | null;
	pendingSectorCount: number | null;
	powerOnHours: number | null;
	/** The serial on the drive's label: what to read off a disk before pulling it. */
	serialNumber: string | null;
	/** ATA 187, Reported_Uncorrect: reads the drive's own ECC could not correct. */
	reportedUncorrect: number | null;
	/** ATA 188, Command_Timeout. Packed counters on Seagate, so only its growth means anything. */
	commandTimeout: number | null;
	/** ATA 199, UDMA_CRC_Error_Count: link errors, usually the cable or the backplane. */
	udmaCrcErrorCount: number | null;
	/** Whether the newest completed self-test passed; null when none is logged. */
	lastSelfTestPassed: boolean | null;
	/** Power-on hours when it ran (some drives wrap this at 16 bits). */
	lastSelfTestHours: number | null;
	sourcePath: string;
	/** When the drive last actually answered. */
	asOf: string;
}

/** One recorded change in a drive's health, from /api/drives/<wwn>/history: written only when a count moved. */
export interface DriveHealthRecord {
	ts: string;
	passed: boolean | null;
	reallocatedSectorCount: number | null;
	pendingSectorCount: number | null;
	powerOnHours: number | null;
	reportedUncorrect: number | null;
	commandTimeout: number | null;
	udmaCrcErrorCount: number | null;
	lastSelfTestPassed: boolean | null;
}

export type Severity = 'info' | 'warning' | 'critical';

export interface EventRecord {
	id: number;
	ts: string;
	severity: Severity;
	/** Stable key for the condition, e.g. "fan-stalled:drive-cage". */
	kind: string;
	message: string;
}

/** A condition that holds now: one line of the verdict (ADR-019). */
export interface Alert {
	/** The condition's stable key, the same kind its events carry. */
	kind: string;
	severity: Severity;
	message: string;
	/** When vigil-core raised it. A restart raises it again, so this is since then at the earliest. */
	since: string;
}

/** What vigil-core remembers about a pool that one zpool status cannot say. */
export interface PoolHistory {
	name: string;
	/** The last completed scrub, which a later resilver hides from zpool. Null when none is known. */
	lastScrub: string | null;
}

// ---- The host's load as vigil-core works it out: rates, null where there is nothing to take one against ----

export interface HostLoad {
	asOf: string;
	/** The seconds the rates span; null when there are none (the first reading, a reboot). */
	intervalSeconds: number | null;
	uptimeSeconds: number | null;
	memory: MemoryLoad | null;
	cpu: CpuLoad | null;
	load: LoadAverage | null;
	pressure: PressureLoad | null;
	disks: DiskLoad[];
	/** Each pool's traffic: the sum of the disks its vdevs are on. */
	pools: PoolLoad[];
	interfaces: InterfaceLoad[];
}

export interface MemoryLoad {
	totalBytes: number;
	/** Total minus MemAvailable, as `free` counts "used". */
	usedBytes: number;
	cachedBytes: number;
	arcBytes: number | null;
	arcMaxBytes: number | null;
	swapTotalBytes: number;
	swapUsedBytes: number;
}

export interface CpuLoad {
	busyPercent: number;
	iowaitPercent: number;
	logicalCpus: number;
}

/** PSI, percent of the time. "Some": at least one task was waiting. "Full": every runnable task was, so the host stood still. */
export interface PressureLoad {
	cpuPercent: number | null;
	memoryPercent: number | null;
	ioPercent: number | null;
	cpuFullPercent: number | null;
	memoryFullPercent: number | null;
	ioFullPercent: number | null;
}

export interface DiskLoad {
	deviceName: string;
	port: string | null;
	readBytesPerSecond: number;
	writtenBytesPerSecond: number;
	/** Percent of the time with I/O in flight, iostat's %util. */
	busyPercent: number;
}

export interface PoolLoad {
	name: string;
	readBytesPerSecond: number;
	writtenBytesPerSecond: number;
}

export interface InterfaceLoad {
	name: string;
	receivedBytesPerSecond: number;
	transmittedBytesPerSecond: number;
}

/** The live stream: what vigil-core sends on /api/live, the current state first, then every change. */
export type LiveMessage =
	| { type: 'snapshot'; snapshot: Snapshot }
	| { type: 'daemon'; connected: boolean }
	| { type: 'event'; event: EventRecord }
	| { type: 'drives'; drives: DriveState[] }
	| { type: 'ups'; ups: UpsState }
	/** Every raised condition, worst first. Empty is what green means. */
	| { type: 'alerts'; alerts: Alert[] }
	| { type: 'pools'; pools: PoolHistory[] }
	/** The host's load, on connect and with each new reading. Null before the first. */
	| { type: 'host'; host: HostLoad | null };

export type RangeKey = '1h' | '6h' | '24h' | '7d' | '30d' | '1y';

/** One series of [epochMillis, value] points. */
export type SeriesPoints = [number, number][];

export interface ChartSeries {
	id: string;
	label: string;
	/** A CSS colour, normally a var(--series-N) token. Colour follows the entity, never its position. */
	color: string;
	points: SeriesPoints;
}

export interface HistoryResponse {
	range: RangeKey;
	from: number;
	to: number;
	bucketSeconds: number;
	/**
	 * Keyed by series id: a sensor id ("drive:<wwn>" follows a disk), a bay ("port:<by-path>"
	 * follows a location, whichever disk is in it), or "drives:max" / "memory:max" for the
	 * hottest of a group.
	 */
	temperatures: Record<string, SeriesPoints>;
	/** Keyed by fan id. */
	duties: Record<string, SeriesPoints>;
	rpms: Record<string, SeriesPoints>;
	/** The UPS: "charge" and "load" (%), "runtime" (minutes), "inputVoltage" (V), "batteryTemperature" (°C), "outputFrequency" (Hz), "outputCurrent" (A), "monitors" (fewest logged in). Empty without NUT. */
	ups: Partial<Record<UpsMetric, SeriesPoints>>;
	/** The host's load (ADR-025): bytes for memory and what it is out of, percent for CPU and pressure. A series is absent until it has a value. */
	host: Partial<Record<HostMetric, SeriesPoints>>;
	/** Bytes per second in, keyed "disk:<name>" (read), "pool:<name>" (read) or "net:<interface>" (received). */
	trafficIn: Record<string, SeriesPoints>;
	/** Bytes per second out under the same keys: written, or transmitted. */
	trafficOut: Record<string, SeriesPoints>;
	/** Percent of the time each "disk:<name>" had I/O in flight. */
	busy: Record<string, SeriesPoints>;
	/** Percent of each pool's raw size allocated, keyed by pool name. */
	poolFill: Record<string, SeriesPoints>;
}

/** `GET /api/status`: whether vigil-core is doing its job, for a monitor rather than the page. */
export interface StatusResponse {
	/** The loop is turning and vigild is connected. The route answers 503 when false. */
	ready: boolean;
	/** The release tag, or "development". */
	version: string;
	daemonConnected: boolean;
	lastSnapshot: string | null;
	databaseReady: boolean;
	loopAgeSeconds: number | null;
}

export type HostMetric =
	| 'memoryUsed'
	| 'memoryCached'
	| 'arc'
	| 'swapUsed'
	| 'cpuBusy'
	| 'cpuIowait'
	| 'load1'
	| 'load5'
	| 'load15'
	| 'pressureCpu'
	| 'pressureMemory'
	| 'pressureIo'
	| 'memoryTotal'
	| 'swapTotal'
	| 'arcMax'
	| 'pressureCpuFull'
	| 'pressureMemoryFull'
	| 'pressureIoFull';

export type UpsMetric =
	'charge' | 'load' | 'runtime' | 'inputVoltage' | 'batteryTemperature' | 'outputFrequency' | 'outputCurrent' | 'monitors';
