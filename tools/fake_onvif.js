// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
//
// A throwaway ONVIF device: enough of the Profile S surface to exercise Neolink's
// client end to end (discovery, device info, media profiles, stream URIs, encoder
// options + write, imaging get/set, PTZ presets + move + absolute zoom, OSDs,
// the cell motion grid, reboot).
// Not a conformant implementation — a test double. Logs every operation it sees.
//
//   node tools/fake_onvif.js <port> [skewSeconds] [modes]
//
// modes is a comma-separated list:
//   media2    the camera speaks ONLY Media2 — every ver10 media call faults
//   zoomonly  the PTZ node is a motorised lens: zoom, no pan or tilt
//   nocells   no analytics service at all, so no motion grid of its own
const http = require('http');

const PORT = Number(process.argv[2] || 8000);
const MODES = new Set((process.argv[4] || '').split(',').filter(Boolean));

// PackBits, as ONVIF's ActiveCells uses it (TIFF 6.0).
const packBits = (bytes) => {
  const out = [];
  let i = 0;
  while (i < bytes.length) {
    let run = 1;
    while (i + run < bytes.length && run < 128 && bytes[i + run] === bytes[i]) run++;
    if (run > 1) { out.push((257 - run) & 0xff, bytes[i]); i += run; continue; }
    const start = i++;
    while (i < bytes.length && i - start < 128 && !(i + 1 < bytes.length && bytes[i] === bytes[i + 1])) i++;
    out.push(i - start - 1, ...bytes.slice(start, i));
  }
  return Buffer.from(out);
};
const unpackBits = (buf) => {
  const out = [];
  for (let i = 0; i < buf.length;) {
    const n = buf[i] > 127 ? buf[i] - 256 : buf[i];
    i++;
    if (n >= 0) { out.push(...buf.slice(i, i + n + 1)); i += n + 1; }
    else if (n !== -128) { for (let k = 0; k < 1 - n; k++) out.push(buf[i]); i++; }
  }
  return out;
};
const CELL_COLS = 22, CELL_ROWS = 18;
// The grid as rows of '0'/'1', so /__state shows what a write actually did.
const cellRows = (b64) => {
  const bytes = unpackBits(Buffer.from(b64, 'base64'));
  const rows = [];
  for (let r = 0; r < CELL_ROWS; r++) {
    let line = '';
    for (let c = 0; c < CELL_COLS; c++) {
      const i = r * CELL_COLS + c;
      line += (bytes[i >> 3] >> (7 - (i & 7))) & 1 ? '1' : '0';
    }
    rows.push(line);
  }
  return rows;
};
const allCells = () => {
  const n = CELL_COLS * CELL_ROWS, bytes = new Array(Math.ceil(n / 8)).fill(0);
  for (let i = 0; i < n; i++) bytes[i >> 3] |= 0x80 >> (i & 7);
  return packBits(bytes).toString('base64');
};

const state = {
  ptz: { zoom: 0.25, panTiltMoves: 0 },
  // The cell motion rule. MinCount and the delays are here so a write can be seen
  // to have kept them.
  cells: { activeCells: allCells(), minCount: 5, alarmOnDelay: 100, alarmOffDelay: 1000, writes: 0 },
  imaging: { Brightness: 50, Contrast: 60, ColorSaturation: 55, Sharpness: 40, IrCutFilter: 'AUTO' },
  encoders: {
    VEC_1: { w: 2560, h: 1440, fps: 20, kbps: 4096, gov: 40, enc: 'H264' },
    VEC_2: { w: 640, h: 360, fps: 15, kbps: 512, gov: 30, enc: 'H264' },
  },
  presets: [{ token: '1', name: 'Driveway' }, { token: '2', name: 'Gate' }],
  osds: {
    OSD_1: { pos: 'UpperLeft', kind: 'Plain', text: 'Fake Cam' },
    OSD_2: { pos: 'LowerRight', kind: 'DateAndTime', text: null },
  },
  // Event notifications queued by POST /__fire, handed out by the next PullMessages.
  pending: [],
  subscriptions: 0,
  renewals: 0,
  seen: [],
};

// How far the camera's clock is from this machine's. A real one with no NTP drifts,
// and the WS-Security digest is only accepted near the camera's own time.
const SKEW_SECONDS = Number(process.argv[3] || 0);

// A 1x1 JPEG, so GetSnapshotUri has something to serve.
const SNAPSHOT = Buffer.from(
  '/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0a' +
  'HBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/wAALCAABAAEBAREA/8QAFAABAAAAAAAA' +
  'AAAAAAAAAAAACf/EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAD8AKp//2Q==', 'base64');

/// One notification, in the shape ONVIF's event service publishes.
const notificationXml = (m) =>
  `<wsnt:NotificationMessage>` +
  `<wsnt:Topic Dialect="http://docs.oasis-open.org/wsn/t-1/TopicExpression/Simple">${m.topic}</wsnt:Topic>` +
  `<wsnt:Message><tt:Message UtcTime="${new Date().toISOString()}">` +
  `<tt:Source><tt:SimpleItem Name="VideoSourceConfigurationToken" Value="VSC_1"/></tt:Source>` +
  `<tt:Data>` +
  Object.entries(m.items || {}).map(([k, v]) => `<tt:SimpleItem Name="${k}" Value="${v}"/>`).join('') +
  `</tt:Data></tt:Message></wsnt:Message></wsnt:NotificationMessage>`;

const NS = {
  s: 'http://www.w3.org/2003/05/soap-envelope',
  tds: 'http://www.onvif.org/ver10/device/wsdl',
  trt: 'http://www.onvif.org/ver10/media/wsdl',
  timg: 'http://www.onvif.org/ver20/imaging/wsdl',
  tptz: 'http://www.onvif.org/ver20/ptz/wsdl',
  tr2: 'http://www.onvif.org/ver20/media/wsdl',
  tan: 'http://www.onvif.org/ver20/analytics/wsdl',
  tev: 'http://www.onvif.org/ver10/events/wsdl',
  wsnt: 'http://docs.oasis-open.org/wsn/b-2',
  wsa: 'http://www.w3.org/2005/08/addressing',
  tt: 'http://www.onvif.org/ver10/schema',
};

const env = (body) =>
  `<?xml version="1.0" encoding="UTF-8"?>` +
  `<s:Envelope xmlns:s="${NS.s}" xmlns:tds="${NS.tds}" xmlns:trt="${NS.trt}" ` +
  `xmlns:timg="${NS.timg}" xmlns:tptz="${NS.tptz}" xmlns:tt="${NS.tt}" ` +
  `xmlns:tev="${NS.tev}" xmlns:wsnt="${NS.wsnt}" xmlns:wsa="${NS.wsa}" ` +
  `xmlns:tr2="${NS.tr2}" xmlns:tan="${NS.tan}">` +
  `<s:Body>${body}</s:Body></s:Envelope>`;

const encoderXml = (token, e) =>
  `<tt:VideoEncoderConfiguration token="${token}">` +
  `<tt:Name>${token}</tt:Name><tt:UseCount>1</tt:UseCount>` +
  `<tt:Encoding>${e.enc}</tt:Encoding>` +
  `<tt:Resolution><tt:Width>${e.w}</tt:Width><tt:Height>${e.h}</tt:Height></tt:Resolution>` +
  `<tt:Quality>4</tt:Quality>` +
  `<tt:RateControl><tt:FrameRateLimit>${e.fps}</tt:FrameRateLimit>` +
  `<tt:EncodingInterval>1</tt:EncodingInterval><tt:BitrateLimit>${e.kbps}</tt:BitrateLimit></tt:RateControl>` +
  `<tt:H264><tt:GovLength>${e.gov}</tt:GovLength><tt:H264Profile>Main</tt:H264Profile></tt:H264>` +
  `<tt:SessionTimeout>PT60S</tt:SessionTimeout>` +
  `</tt:VideoEncoderConfiguration>`;

const osdXml = (token, o) =>
  `<trt:OSDs token="${token}">` +
  `<tt:VideoSourceConfigurationToken>VSC_1</tt:VideoSourceConfigurationToken>` +
  `<tt:Type>Text</tt:Type>` +
  `<tt:Position><tt:Type>${o.pos}</tt:Type></tt:Position>` +
  `<tt:TextString><tt:Type>${o.kind}</tt:Type>` +
  (o.text != null ? `<tt:PlainText>${o.text}</tt:PlainText>` : '') +
  `<tt:FontSize>30</tt:FontSize></tt:TextString>` +
  `</trt:OSDs>`;

// Local-name lookups: the client's own namespace prefixes are its business.
const pick = (xml, name) => {
  const m = xml.match(new RegExp(`<(?:[\\w.-]+:)?${name}[^>]*>([\\s\\S]*?)</(?:[\\w.-]+:)?${name}>`));
  return m ? m[1].trim() : null;
};
const attrToken = (xml, name) => {
  const m = xml.match(new RegExp(`<(?:[\\w.-]+:)?${name}\\b[^>]*token="([^"]+)"`));
  return m ? m[1] : null;
};

// The encoder write both media dialects share: ver10 wraps it in trt:Configuration,
// Media2 in tr2:Configuration with GovLength as an attribute.
function applyEncoder(body) {
  const token = attrToken(body, 'Configuration');
  const e = state.encoders[token];
  if (!e) return null;
  const res = body.match(/<(?:[\w.-]+:)?Resolution[^>]*>([\s\S]*?)<\/(?:[\w.-]+:)?Resolution>/);
  if (res) {
    e.w = Number(pick(res[1] + '', 'Width')) || e.w;
    e.h = Number(pick(res[1] + '', 'Height')) || e.h;
  }
  const fps = pick(body, 'FrameRateLimit');
  const kbps = pick(body, 'BitrateLimit');
  if (fps) e.fps = Number(fps);
  if (kbps) e.kbps = Number(kbps);
  console.log(`  -> encoder ${token} now ${e.w}x${e.h} ${e.fps}fps ${e.kbps}kbps`);
  return e;
}

const encoder2Xml = (token, e) =>
  `<tr2:VideoEncoder token="${token}" GovLength="${e.gov}" Profile="Main">` +
  `<tt:Name>${token}</tt:Name><tt:UseCount>1</tt:UseCount>` +
  `<tt:Encoding>${e.enc}</tt:Encoding>` +
  `<tt:Resolution><tt:Width>${e.w}</tt:Width><tt:Height>${e.h}</tt:Height></tt:Resolution>` +
  `<tt:RateControl ConstantBitRate="false"><tt:FrameRateLimit>${e.fps}</tt:FrameRateLimit>` +
  `<tt:BitrateLimit>${e.kbps}</tt:BitrateLimit></tt:RateControl>` +
  `<tt:Quality>4</tt:Quality></tr2:VideoEncoder>`;

// Media2, in its own dialect: parts under Configurations, a bare Uri, and one
// Options block per codec with the frame rates listed rather than ranged.
function handleMedia2(op, body) {
  switch (op) {
    case 'GetProfiles':
      return `<tr2:GetProfilesResponse>` +
        `<tr2:Profiles token="Profile_1" fixed="true"><tr2:Name>mainstream</tr2:Name><tr2:Configurations>` +
        `<tr2:VideoSource token="VSC_1"><tt:Name>VSC</tt:Name><tt:SourceToken>VS_1</tt:SourceToken></tr2:VideoSource>` +
        encoder2Xml('VEC_1', state.encoders.VEC_1) +
        (MODES.has('nocells') ? '' : `<tr2:Analytics token="VAC_1"><tt:Name>va</tt:Name></tr2:Analytics>`) +
        `<tr2:PTZ token="PTZ_1"><tt:Name>ptz</tt:Name></tr2:PTZ>` +
        `</tr2:Configurations></tr2:Profiles>` +
        `<tr2:Profiles token="Profile_2"><tr2:Name>substream</tr2:Name><tr2:Configurations>` +
        `<tr2:VideoSource token="VSC_1"><tt:Name>VSC</tt:Name><tt:SourceToken>VS_1</tt:SourceToken></tr2:VideoSource>` +
        encoder2Xml('VEC_2', state.encoders.VEC_2) +
        `</tr2:Configurations></tr2:Profiles></tr2:GetProfilesResponse>`;
    case 'GetStreamUri': {
      const p = pick(body, 'ProfileToken');
      const path = p === 'Profile_1' ? '/Driveway' : '/FrontDoor';
      return `<tr2:GetStreamUriResponse><tr2:Uri>rtsp://camhost:554${path}</tr2:Uri></tr2:GetStreamUriResponse>`;
    }
    case 'GetVideoEncoderConfigurationOptions':
      return `<tr2:GetVideoEncoderConfigurationOptionsResponse>` +
        `<tr2:Options GovLengthRange="1 100" FrameRatesSupported="25 20 15 12.5 10 5 1" ProfilesSupported="Main High">` +
        `<tt:Encoding>H264</tt:Encoding>` +
        `<tt:QualityRange><tt:Min>1</tt:Min><tt:Max>6</tt:Max></tt:QualityRange>` +
        `<tt:ResolutionsAvailable><tt:Width>640</tt:Width><tt:Height>360</tt:Height></tt:ResolutionsAvailable>` +
        `<tt:ResolutionsAvailable><tt:Width>1920</tt:Width><tt:Height>1080</tt:Height></tt:ResolutionsAvailable>` +
        `<tt:ResolutionsAvailable><tt:Width>2560</tt:Width><tt:Height>1440</tt:Height></tt:ResolutionsAvailable>` +
        `<tt:BitrateRange><tt:Min>256</tt:Min><tt:Max>8192</tt:Max></tt:BitrateRange>` +
        `</tr2:Options>` +
        `<tr2:Options GovLengthRange="1 100" FrameRatesSupported="15 10 5">` +
        `<tt:Encoding>H265</tt:Encoding>` +
        `<tt:ResolutionsAvailable><tt:Width>3840</tt:Width><tt:Height>2160</tt:Height></tt:ResolutionsAvailable>` +
        `<tt:BitrateRange><tt:Min>512</tt:Min><tt:Max>16384</tt:Max></tt:BitrateRange>` +
        `</tr2:Options></tr2:GetVideoEncoderConfigurationOptionsResponse>`;
    case 'SetVideoEncoderConfiguration':
      if (/ForcePersistence/.test(body)) return fault('Media2 SetVideoEncoderConfiguration has no ForcePersistence');
      return applyEncoder(body) ? `<tr2:SetVideoEncoderConfigurationResponse/>` : fault('no such configuration');
    case 'GetSnapshotUri':
      return `<tr2:GetSnapshotUriResponse><tr2:Uri>http://127.0.0.1:${PORT}/snapshot.jpg</tr2:Uri></tr2:GetSnapshotUriResponse>`;
    case 'GetOSDs':
      return `<tr2:GetOSDsResponse>` +
        Object.entries(state.osds).map(([t, o]) => osdXml(t, o).replace(/trt:OSDs/g, 'tr2:OSDs')).join('') +
        `</tr2:GetOSDsResponse>`;
    default:
      return null;
  }
}

function handle(op, body, url) {
  state.seen.push(op);
  // Which media dialect is served where. A Media2-only camera has no ver10 media
  // service at all, so everything sent there faults — as it would on the real thing.
  if (url.startsWith('/onvif/media2_service'))
    return MODES.has('media2') ? handleMedia2(op, body) : null;
  if (url.startsWith('/onvif/media_service') && MODES.has('media2'))
    return null;
  switch (op) {
    case 'GetCapabilities':
      return `<tds:GetCapabilitiesResponse><tds:Capabilities>` +
        (MODES.has('nocells') ? '' :
          `<tt:Analytics><tt:XAddr>http://127.0.0.1:${PORT}/onvif/analytics_service</tt:XAddr>` +
          `<tt:RuleSupport>true</tt:RuleSupport><tt:AnalyticsModuleSupport>true</tt:AnalyticsModuleSupport></tt:Analytics>`) +
        `<tt:Device><tt:XAddr>http://CAMHOST/onvif/device_service</tt:XAddr></tt:Device>` +
        (MODES.has('media2') ? '' :
          `<tt:Media><tt:XAddr>http://127.0.0.1:${PORT}/onvif/media_service</tt:XAddr></tt:Media>`) +
        `<tt:Events><tt:XAddr>http://127.0.0.1:${PORT}/onvif/events_service</tt:XAddr>` +
        `<tt:WSPullPointSupport>true</tt:WSPullPointSupport></tt:Events>` +
        `<tt:Imaging><tt:XAddr>http://127.0.0.1:${PORT}/onvif/imaging</tt:XAddr></tt:Imaging>` +
        `<tt:PTZ><tt:XAddr>http://127.0.0.1:${PORT}/onvif/ptz_service</tt:XAddr></tt:PTZ>` +
        `</tds:Capabilities></tds:GetCapabilitiesResponse>`;
    case 'GetServices': {
      const svc = (ns, path) =>
        `<tds:Service><tds:Namespace>${ns}</tds:Namespace>` +
        `<tds:XAddr>http://127.0.0.1:${PORT}/onvif/${path}</tds:XAddr>` +
        `<tds:Version><tt:Major>2</tt:Major><tt:Minor>60</tt:Minor></tds:Version></tds:Service>`;
      return `<tds:GetServicesResponse>` +
        svc(NS.tds, 'device_service') +
        (MODES.has('media2') ? svc(NS.tr2, 'media2_service') : svc(NS.trt, 'media_service')) +
        svc(NS.timg, 'imaging') + svc(NS.tptz, 'ptz_service') + svc(NS.tev, 'events_service') +
        (MODES.has('nocells') ? '' : svc(NS.tan, 'analytics_service')) +
        `</tds:GetServicesResponse>`;
    }
    // ---- PTZ node, status and absolute zoom
    case 'GetNodes':
      return `<tptz:GetNodesResponse><tptz:PTZNode token="Node_1" FixedHomePosition="false">` +
        `<tt:Name>head</tt:Name><tt:SupportedPTZSpaces>` +
        `<tt:AbsoluteZoomPositionSpace><tt:URI>http://www.onvif.org/ver10/tptz/ZoomSpaces/PositionGenericSpace</tt:URI>` +
        `<tt:XRange><tt:Min>0</tt:Min><tt:Max>1</tt:Max></tt:XRange></tt:AbsoluteZoomPositionSpace>` +
        (MODES.has('zoomonly') ? '' :
          `<tt:ContinuousPanTiltVelocitySpace><tt:URI>http://www.onvif.org/ver10/tptz/PanTiltSpaces/VelocityGenericSpace</tt:URI>` +
          `<tt:XRange><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:XRange>` +
          `<tt:YRange><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:YRange></tt:ContinuousPanTiltVelocitySpace>`) +
        `</tt:SupportedPTZSpaces><tt:MaximumNumberOfPresets>16</tt:MaximumNumberOfPresets>` +
        `<tt:HomeSupported>false</tt:HomeSupported></tptz:PTZNode></tptz:GetNodesResponse>`;
    case 'GetStatus':
      return `<tptz:GetStatusResponse><tptz:PTZStatus><tt:Position>` +
        `<tt:PanTilt x="0" y="0"/><tt:Zoom x="${state.ptz.zoom}"/></tt:Position>` +
        `<tt:MoveStatus><tt:PanTilt>IDLE</tt:PanTilt><tt:Zoom>IDLE</tt:Zoom></tt:MoveStatus>` +
        `<tt:UtcTime>${new Date().toISOString()}</tt:UtcTime></tptz:PTZStatus></tptz:GetStatusResponse>`;
    case 'AbsoluteMove': {
      // A zoom slider must never swing the head: a PanTilt here would.
      if (/<(?:[\w.-]+:)?PanTilt\b/.test(body)) state.ptz.panTiltMoves++;
      const z = body.match(/<(?:[\w.-]+:)?Zoom\b[^>]*\bx="([^"]+)"/);
      if (!z) return fault('AbsoluteMove without a Zoom position');
      const x = Number(z[1]);
      if (!(x >= 0 && x <= 1)) return fault('zoom out of range: ' + z[1]);
      state.ptz.zoom = x;
      console.log(`  -> zoom now ${x}`);
      return `<tptz:AbsoluteMoveResponse/>`;
    }
    // ---- analytics: the cell motion grid
    case 'GetAnalyticsModules':
      if (pick(body, 'ConfigurationToken') !== 'VAC_1') return fault('no such analytics configuration');
      return `<tan:GetAnalyticsModulesResponse>` +
        `<tan:AnalyticsModule Name="MyTamperModule" Type="tt:TamperEngine"/>` +
        `<tan:AnalyticsModule Name="MyCellMotionModule" Type="tt:CellMotionEngine"><tt:Parameters>` +
        `<tt:SimpleItem Name="Sensitivity" Value="60"/>` +
        `<tt:ElementItem Name="Layout"><tt:CellLayout Columns="${CELL_COLS}" Rows="${CELL_ROWS}">` +
        `<tt:Transformation><tt:Translate x="-1.0" y="-1.0"/><tt:Scale x="0.090909" y="0.111111"/></tt:Transformation>` +
        `</tt:CellLayout></tt:ElementItem></tt:Parameters></tan:AnalyticsModule>` +
        `</tan:GetAnalyticsModulesResponse>`;
    case 'GetRules':
      if (pick(body, 'ConfigurationToken') !== 'VAC_1') return fault('no such analytics configuration');
      return `<tan:GetRulesResponse>` +
        `<tan:Rule Name="MyLineRule" Type="tt:LineDetector"><tt:Parameters>` +
        `<tt:SimpleItem Name="Direction" Value="Any"/></tt:Parameters></tan:Rule>` +
        `<tan:Rule Name="MyMotionDetectorRule" Type="tt:CellMotionDetector"><tt:Parameters>` +
        `<tt:SimpleItem Name="MinCount" Value="${state.cells.minCount}"/>` +
        `<tt:SimpleItem Name="AlarmOnDelay" Value="${state.cells.alarmOnDelay}"/>` +
        `<tt:SimpleItem Name="AlarmOffDelay" Value="${state.cells.alarmOffDelay}"/>` +
        `<tt:SimpleItem Name="ActiveCells" Value="${state.cells.activeCells}"/>` +
        `</tt:Parameters></tan:Rule></tan:GetRulesResponse>`;
    case 'ModifyRules': {
      // A strict camera: the rule's Type is a QName, and its prefix must resolve
      // inside the request itself.
      const rule = body.match(/<(?:[\w.-]+:)?Rule\b([^>]*)>/);
      const type = rule && rule[1].match(/\bType="([^"]+)"/);
      if (!type) return fault('rule without a Type');
      const [pfx, local] = type[1].includes(':') ? type[1].split(':') : ['', type[1]];
      const declared = new RegExp(`xmlns:${pfx}="${NS.tt.replace(/[.]/g, '[.]')}"`).test(body);
      if (local !== 'CellMotionDetector' || (pfx && !declared))
        return fault(`rule Type ${type[1]} does not resolve to tt:CellMotionDetector`);
      const item = (n) => (body.match(new RegExp(`Name="${n}"\\s+Value="([^"]*)"`)) || [])[1];
      const cells = item('ActiveCells');
      if (!cells) return fault('no ActiveCells');
      const need = Math.ceil(CELL_COLS * CELL_ROWS / 8);
      if (unpackBits(Buffer.from(cells, 'base64')).length < need) return fault('ActiveCells too short for the layout');
      state.cells.activeCells = cells;
      state.cells.minCount = Number(item('MinCount') ?? -1);
      state.cells.alarmOnDelay = Number(item('AlarmOnDelay') ?? -1);
      state.cells.alarmOffDelay = Number(item('AlarmOffDelay') ?? -1);
      state.cells.writes++;
      console.log('  -> motion grid now\n    ' + cellRows(cells).join('\n    '));
      return `<tan:ModifyRulesResponse/>`;
    }
    case 'GetDeviceInformation':
      return `<tds:GetDeviceInformationResponse>` +
        `<tds:Manufacturer>Acme</tds:Manufacturer><tds:Model>AC-4K-Dome</tds:Model>` +
        `<tds:FirmwareVersion>V1.2.3</tds:FirmwareVersion>` +
        `<tds:SerialNumber>SN-000123</tds:SerialNumber><tds:HardwareId>HW7</tds:HardwareId>` +
        `</tds:GetDeviceInformationResponse>`;
    case 'GetVideoSources':
      return `<trt:GetVideoSourcesResponse><trt:VideoSources token="VS_1">` +
        `<tt:Framerate>25</tt:Framerate>` +
        `<tt:Resolution><tt:Width>2560</tt:Width><tt:Height>1440</tt:Height></tt:Resolution>` +
        `</trt:VideoSources></trt:GetVideoSourcesResponse>`;
    case 'GetProfiles':
      return `<trt:GetProfilesResponse>` +
        `<trt:Profiles token="Profile_1" fixed="true"><tt:Name>mainstream</tt:Name>` +
        `<tt:VideoSourceConfiguration token="VSC_1"><tt:Name>VSC</tt:Name></tt:VideoSourceConfiguration>` +
        encoderXml('VEC_1', state.encoders.VEC_1) +
        (MODES.has('nocells') ? '' : `<tt:VideoAnalyticsConfiguration token="VAC_1"><tt:Name>va</tt:Name></tt:VideoAnalyticsConfiguration>`) +
        `<tt:PTZConfiguration token="PTZ_1"><tt:Name>ptz</tt:Name></tt:PTZConfiguration>` +
        `</trt:Profiles>` +
        `<trt:Profiles token="Profile_2"><tt:Name>substream</tt:Name>` +
        `<tt:VideoSourceConfiguration token="VSC_1"><tt:Name>VSC</tt:Name></tt:VideoSourceConfiguration>` +
        encoderXml('VEC_2', state.encoders.VEC_2) +
        `</trt:Profiles></trt:GetProfilesResponse>`;
    case 'GetStreamUri': {
      const p = pick(body, 'ProfileToken');
      const path = p === 'Profile_1' ? '/Driveway' : '/FrontDoor';
      return `<trt:GetStreamUriResponse><trt:MediaUri>` +
        `<tt:Uri>rtsp://camhost:554${path}</tt:Uri>` +
        `<tt:InvalidAfterConnect>false</tt:InvalidAfterConnect>` +
        `</trt:MediaUri></trt:GetStreamUriResponse>`;
    }
    case 'GetVideoEncoderConfigurationOptions':
      return `<trt:GetVideoEncoderConfigurationOptionsResponse><trt:Options>` +
        `<tt:QualityRange><tt:Min>1</tt:Min><tt:Max>6</tt:Max></tt:QualityRange>` +
        `<tt:H264>` +
        `<tt:ResolutionsAvailable><tt:Width>640</tt:Width><tt:Height>360</tt:Height></tt:ResolutionsAvailable>` +
        `<tt:ResolutionsAvailable><tt:Width>1920</tt:Width><tt:Height>1080</tt:Height></tt:ResolutionsAvailable>` +
        `<tt:ResolutionsAvailable><tt:Width>2560</tt:Width><tt:Height>1440</tt:Height></tt:ResolutionsAvailable>` +
        `<tt:GovLengthRange><tt:Min>1</tt:Min><tt:Max>100</tt:Max></tt:GovLengthRange>` +
        `<tt:FrameRateRange><tt:Min>1</tt:Min><tt:Max>25</tt:Max></tt:FrameRateRange>` +
        `</tt:H264>` +
        `<tt:Extension><tt:H264><tt:BitrateRange><tt:Min>256</tt:Min><tt:Max>8192</tt:Max></tt:BitrateRange></tt:H264></tt:Extension>` +
        `</trt:Options></trt:GetVideoEncoderConfigurationOptionsResponse>`;
    case 'SetVideoEncoderConfiguration':
      return applyEncoder(body) ? `<trt:SetVideoEncoderConfigurationResponse/>` : fault('no such configuration');
    case 'GetOptions':
      return `<timg:GetOptionsResponse><timg:ImagingOptions>` +
        `<tt:Brightness><tt:Min>0</tt:Min><tt:Max>100</tt:Max></tt:Brightness>` +
        `<tt:ColorSaturation><tt:Min>0</tt:Min><tt:Max>100</tt:Max></tt:ColorSaturation>` +
        `<tt:Contrast><tt:Min>0</tt:Min><tt:Max>100</tt:Max></tt:Contrast>` +
        `<tt:Sharpness><tt:Min>0</tt:Min><tt:Max>100</tt:Max></tt:Sharpness>` +
        `</timg:ImagingOptions></timg:GetOptionsResponse>`;
    case 'GetImagingSettings':
      return `<timg:GetImagingSettingsResponse><timg:ImagingSettings>` +
        `<tt:Brightness>${state.imaging.Brightness}</tt:Brightness>` +
        `<tt:ColorSaturation>${state.imaging.ColorSaturation}</tt:ColorSaturation>` +
        `<tt:Contrast>${state.imaging.Contrast}</tt:Contrast>` +
        `<tt:IrCutFilter>${state.imaging.IrCutFilter}</tt:IrCutFilter>` +
        `<tt:Sharpness>${state.imaging.Sharpness}</tt:Sharpness>` +
        `</timg:ImagingSettings></timg:GetImagingSettingsResponse>`;
    case 'SetImagingSettings': {
      for (const f of ['Brightness', 'ColorSaturation', 'Contrast', 'Sharpness']) {
        const v = pick(body, f);
        if (v != null) state.imaging[f] = Number(v);
      }
      const ir = pick(body, 'IrCutFilter');
      if (ir) state.imaging.IrCutFilter = ir;
      console.log('  -> imaging now', JSON.stringify(state.imaging));
      return `<timg:SetImagingSettingsResponse/>`;
    }
    case 'GetPresets':
      return `<tptz:GetPresetsResponse>` +
        state.presets.map(p => `<tptz:Preset token="${p.token}"><tt:Name>${p.name}</tt:Name></tptz:Preset>`).join('') +
        `</tptz:GetPresetsResponse>`;
    case 'GotoPreset':
      console.log('  -> goto preset', pick(body, 'PresetToken'));
      return `<tptz:GotoPresetResponse/>`;
    case 'SetPreset': {
      const name = pick(body, 'PresetName');
      const token = pick(body, 'PresetToken');
      if (token) {
        const p = state.presets.find(p => p.token === token);
        if (p) p.name = name;
      } else {
        state.presets.push({ token: String(state.presets.length + 1), name });
      }
      console.log('  -> presets now', JSON.stringify(state.presets));
      return `<tptz:SetPresetResponse><tptz:PresetToken>${token || state.presets.length}</tptz:PresetToken></tptz:SetPresetResponse>`;
    }
    case 'ContinuousMove':
      console.log('  -> move', (body.match(/<(?:[\w.-]+:)?PanTilt[^>]*>/) || [''])[0],
        (body.match(/<(?:[\w.-]+:)?Zoom[^>]*>/) || [''])[0]);
      return `<tptz:ContinuousMoveResponse/>`;
    case 'Stop':
      return `<tptz:StopResponse/>`;
    case 'GetOSDs':
      return `<trt:GetOSDsResponse>` +
        Object.entries(state.osds).map(([t, o]) => osdXml(t, o)).join('') +
        `</trt:GetOSDsResponse>`;
    case 'SetOSD': {
      const token = attrToken(body, 'OSD');
      const o = state.osds[token];
      if (!o) return fault('no such OSD ' + token);
      const posBlock = body.match(/<(?:[\w.-]+:)?Position[^>]*>([\s\S]*?)<\/(?:[\w.-]+:)?Position>/);
      if (posBlock) o.pos = pick(posBlock[1], 'Type') || o.pos;
      const text = pick(body, 'PlainText');
      if (text != null) o.text = text;
      console.log(`  -> OSD ${token} now`, JSON.stringify(o));
      return `<trt:SetOSDResponse/>`;
    }
    case 'GetSystemDateAndTime': {
      // Deliberately skewed, so the client's clock correction is exercised: a real
      // camera with no NTP drifts, and a WS-Security digest stamped in OUR time
      // would then be rejected by it.
      const t = new Date(Date.now() + SKEW_SECONDS * 1000);
      return `<tds:GetSystemDateAndTimeResponse><tds:SystemDateAndTime>` +
        `<tt:DateTimeType>NTP</tt:DateTimeType><tt:DaylightSavings>false</tt:DaylightSavings>` +
        `<tt:UTCDateTime><tt:Time>` +
        `<tt:Hour>${t.getUTCHours()}</tt:Hour><tt:Minute>${t.getUTCMinutes()}</tt:Minute>` +
        `<tt:Second>${t.getUTCSeconds()}</tt:Second></tt:Time><tt:Date>` +
        `<tt:Year>${t.getUTCFullYear()}</tt:Year><tt:Month>${t.getUTCMonth() + 1}</tt:Month>` +
        `<tt:Day>${t.getUTCDate()}</tt:Day></tt:Date></tt:UTCDateTime>` +
        `</tds:SystemDateAndTime></tds:GetSystemDateAndTimeResponse>`;
    }
    case 'GetSnapshotUri':
      return `<trt:GetSnapshotUriResponse><trt:MediaUri>` +
        `<tt:Uri>http://127.0.0.1:${PORT}/snapshot.jpg</tt:Uri>` +
        `<tt:InvalidAfterConnect>false</tt:InvalidAfterConnect>` +
        `</trt:MediaUri></trt:GetSnapshotUriResponse>`;
    case 'CreatePullPointSubscription':
      state.subscriptions++;
      return `<tev:CreatePullPointSubscriptionResponse>` +
        `<tev:SubscriptionReference><wsa:Address>http://127.0.0.1:${PORT}/onvif/subscription_0` +
        `</wsa:Address></tev:SubscriptionReference>` +
        `<wsnt:CurrentTime>${new Date().toISOString()}</wsnt:CurrentTime>` +
        `<wsnt:TerminationTime>${new Date(Date.now() + 60000).toISOString()}</wsnt:TerminationTime>` +
        `</tev:CreatePullPointSubscriptionResponse>`;
    case 'PullMessages': {
      // Whatever has been queued through /__fire since the last pull. A real camera
      // would hold the request open; answering at once is what a busy one does too,
      // and it keeps the test quick.
      const msgs = state.pending.splice(0);
      return `<tev:PullMessagesResponse>` +
        `<tev:CurrentTime>${new Date().toISOString()}</tev:CurrentTime>` +
        `<tev:TerminationTime>${new Date(Date.now() + 60000).toISOString()}</tev:TerminationTime>` +
        msgs.map(m => notificationXml(m)).join('') +
        `</tev:PullMessagesResponse>`;
    }
    case 'Renew':
      state.renewals++;
      return `<wsnt:RenewResponse>` +
        `<wsnt:TerminationTime>${new Date(Date.now() + 60000).toISOString()}</wsnt:TerminationTime>` +
        `</wsnt:RenewResponse>`;
    case 'Unsubscribe':
      return `<wsnt:UnsubscribeResponse/>`;
    case 'SystemReboot':
      console.log('  -> REBOOT requested');
      return `<tds:SystemRebootResponse><tds:Message>rebooting</tds:Message></tds:SystemRebootResponse>`;
    default:
      return null; // unimplemented -> SOAP fault, like a real camera
  }
}

const fault = (reason) =>
  `<s:Fault xmlns:s="${NS.s}"><s:Code><s:Value>s:Receiver</s:Value></s:Code>` +
  `<s:Reason><s:Text xml:lang="en">${reason}</s:Text></s:Reason></s:Fault>`;

http.createServer((req, res) => {
  if (req.method === 'GET' && req.url === '/__state') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ ...state, modes: [...MODES], grid: cellRows(state.cells.activeCells) }, null, 2));
    return;
  }
  if (req.url === '/snapshot.jpg') {
    res.writeHead(200, { 'Content-Type': 'image/jpeg', 'Content-Length': SNAPSHOT.length });
    res.end(SNAPSHOT);
    return;
  }
  // POST /__fire with {topic, items} queues one notification for the next
  // PullMessages — this is how a test makes the camera "see" something.
  if (req.method === 'POST' && req.url === '/__fire') {
    let raw = '';
    req.on('data', d => (raw += d));
    req.on('end', () => {
      try {
        const m = JSON.parse(raw);
        state.pending.push({ topic: m.topic, items: m.items || {} });
        console.log(`  <- fired ${m.topic} ${JSON.stringify(m.items || {})}`);
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ queued: state.pending.length }));
      } catch (e) {
        res.writeHead(400); res.end(String(e));
      }
    });
    return;
  }
  let body = '';
  req.on('data', d => (body += d));
  req.on('end', () => {
    // The operation is the first child of s:Body.
    const m = body.match(/<(?:[\w.-]+:)?Body>\s*<(?:[\w.-]+:)?([A-Za-z]+)/);
    const op = m ? m[1] : '(none)';
    const auth = /UsernameToken/.test(body) ? 'wsse' : 'anon';
    // A real camera accepts a WS-Security digest only near its OWN clock. With
    // --skew set, this enforces that, so a client that stamps requests in the
    // server's time instead of the camera's fails here exactly as it would in the
    // field — which is the whole point of the check.
    if (SKEW_SECONDS && auth === 'wsse' && op !== 'GetSystemDateAndTime') {
      const m = body.match(/<(?:[\w.-]+:)?Created[^>]*>([^<]+)<\//);
      const stamped = m ? Date.parse(m[1]) : NaN;
      const cameraNow = Date.now() + SKEW_SECONDS * 1000;
      const off = Math.abs(cameraNow - stamped) / 1000;
      if (!(off <= 10)) {
        console.log(`  !! ${op} REJECTED: stamped ${off.toFixed(0)}s from the camera's clock`);
        state.rejected = (state.rejected || 0) + 1;
        res.writeHead(400, { 'Content-Type': 'application/soap+xml' });
        res.end(env(fault('The security token could not be authenticated: stale timestamp')));
        return;
      }
    }
    console.log(`${req.url}  ${op}  [${auth}]`);
    if (/^Set/.test(op)) {
      const inner = body.match(/<(?:[\w.-]+:)?Body>([\s\S]*)<\/(?:[\w.-]+:)?Body>/);
      console.log('    BODY: ' + (inner ? inner[1] : body));
    }
    const out = handle(op, body, req.url);
    if (out == null) {
      res.writeHead(500, { 'Content-Type': 'application/soap+xml' });
      res.end(env(fault(`operation ${op} is not implemented`)));
      return;
    }
    res.writeHead(200, { 'Content-Type': 'application/soap+xml' });
    res.end(env(out));
  });
}).listen(PORT, '127.0.0.1', () => console.log(`fake ONVIF device on :${PORT}`));
