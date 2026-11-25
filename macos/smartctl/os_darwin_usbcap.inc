/*
 * os_darwin_usbcap.inc
 *
 * WipeKit addition to smartmontools (os_darwin.cpp).
 * Copyright (C) 2026 mllsm66
 *
 * SPDX-License-Identifier: GPL-2.0-or-later
 *
 * macOS has no SCSI pass-through for USB mass storage devices, so SAT and
 * the vendor specific USB bridge commands (JMicron, ASMedia, Realtek, ...)
 * never reach the drive. This file adds the device type "usbcap":
 *
 *   smartctl -x usbcap:/dev/disk4        (or usbcap:disk4)
 *   smartctl -x usbcap:0x14c00000        (USB location ID)
 *
 * The whole USB device is taken over from the macOS mass storage driver
 * ("capture", requires root), SCSI commands are sent with the USB Bulk-Only
 * Transport protocol, and on close the device is handed back to macOS
 * (re-enumeration). The drive disappears for a few seconds and then comes
 * back. To avoid data loss the device is refused while any of its volumes
 * is mounted.
 *
 * Environment: WIPEKIT_USBCAP_KEEP=1 keeps the device captured on close, so
 * several smartctl runs can follow each other. The last run must be made
 * without it to give the device back.
 */

#include <IOKit/usb/IOUSBLib.h>
#include <sys/param.h>
#include <sys/mount.h>
#include <string>
#include <vector>

#ifndef kUSBReEnumerateCaptureDeviceMask
#define kUSBReEnumerateCaptureDeviceMask (1U << 30)
#endif

namespace usbcap {

typedef IOUSBDeviceInterface320 ** devif_t;
typedef IOUSBInterfaceInterface300 ** intf_t;

static bool get_u32(io_registry_entry_t e, const char * key, uint32_t & val)
{
  CFStringRef k = CFStringCreateWithCString(kCFAllocatorDefault, key, kCFStringEncodingUTF8);
  if (!k)
    return false;
  CFTypeRef r = IORegistryEntryCreateCFProperty(e, k, kCFAllocatorDefault, 0);
  CFRelease(k);
  bool ok = false;
  if (r) {
    if (CFGetTypeID(r) == CFNumberGetTypeID()) {
      long long v = 0;
      ok = CFNumberGetValue((CFNumberRef)r, kCFNumberLongLongType, &v);
      val = (uint32_t)v;
    }
    CFRelease(r);
  }
  return ok;
}

static bool is_usb_device(io_registry_entry_t e)
{
  return IOObjectConformsTo(e, "IOUSBHostDevice") || IOObjectConformsTo(e, "IOUSBDevice");
}

struct usb_id {
  uint32_t loc = 0, vid = 0, pid = 0, bcd = 0;
};

static void read_ids(io_registry_entry_t e, usb_id & id)
{
  get_u32(e, "locationID", id.loc);
  get_u32(e, "idVendor", id.vid);
  get_u32(e, "idProduct", id.pid);
  get_u32(e, "bcdDevice", id.bcd);
}

// Walks from a BSD disk ("disk4") up to its USB device.
static bool ids_for_bsd(const char * bsdname, usb_id & id)
{
  CFMutableDictionaryRef m = IOBSDNameMatching(kIOMasterPortDefault, 0, bsdname);
  if (!m)
    return false;
  io_registry_entry_t e = IOServiceGetMatchingService(kIOMasterPortDefault, m);
  bool found = false;
  while (e) {
    if (is_usb_device(e)) {
      read_ids(e, id);
      found = (id.loc != 0);
      IOObjectRelease(e);
      break;
    }
    io_registry_entry_t parent = 0;
    kern_return_t kr = IORegistryEntryGetParentEntry(e, kIOServicePlane, &parent);
    IOObjectRelease(e);
    e = (kr == KERN_SUCCESS ? parent : 0);
  }
  return found;
}

// Finds the USB device with the given location ID (caller releases).
static io_service_t service_for_location(uint32_t loc)
{
  const char * classes[] = { "IOUSBHostDevice", "IOUSBDevice" };
  for (const char * cls : classes) {
    io_iterator_t it = 0;
    if (IOServiceGetMatchingServices(kIOMasterPortDefault, IOServiceMatching(cls), &it) != KERN_SUCCESS)
      continue;
    io_service_t s, hit = 0;
    while ((s = IOIteratorNext(it))) {
      uint32_t l = 0;
      if (!hit && get_u32(s, "locationID", l) && l == loc) {
        hit = s;
        continue;
      }
      IOObjectRelease(s);
    }
    IOObjectRelease(it);
    if (hit)
      return hit;
  }
  return 0;
}

// Parses "usbcap:/dev/disk4", "usbcap:disk4", "usbcap:0x14c00000".
static bool resolve(const char * name, usb_id & id)
{
  if (strncmp(name, "usbcap:", 7))
    return false;
  const char * p = name + 7;
  if (!strncmp(p, "0x", 2) || !strncmp(p, "0X", 2)) {
    char * end = 0;
    unsigned long v = strtoul(p, &end, 16);
    if (!end || *end || !v)
      return false;
    io_service_t s = service_for_location((uint32_t)v);
    if (!s)
      return false;
    read_ids(s, id);
    IOObjectRelease(s);
    return id.loc == (uint32_t)v;
  }
  if (!strncmp(p, "/dev/r", 6))
    p += 6;
  else if (!strncmp(p, "/dev/", 5))
    p += 5;
  if (strncmp(p, "disk", 4))
    return false;
  return ids_for_bsd(p, id);
}

// True if any mounted file system lives on this USB device.
static bool has_mounts(uint32_t loc, std::string & where)
{
  struct statfs * mnts = 0;
  int n = getmntinfo(&mnts, MNT_NOWAIT);
  for (int i = 0; i < n; i++) {
    const char * from = mnts[i].f_mntfromname;
    if (strncmp(from, "/dev/disk", 9))
      continue;
    usb_id id;
    if (ids_for_bsd(from + 5, id) && id.loc == loc) {
      where = mnts[i].f_mntonname;
      return true;
    }
  }
  return false;
}

static void put32(uint8_t * p, uint32_t v)
{
  p[0] = v & 0xff; p[1] = (v >> 8) & 0xff; p[2] = (v >> 16) & 0xff; p[3] = (v >> 24) & 0xff;
}

static uint32_t get32(const uint8_t * p)
{
  return p[0] | (p[1] << 8) | (p[2] << 16) | ((uint32_t)p[3] << 24);
}

} // namespace usbcap

class darwin_usbcap_device
: public /*implements*/ scsi_device
{
public:
  darwin_usbcap_device(smart_interface * intf, const char * dev_name, const char * req_type);
  virtual ~darwin_usbcap_device();

  virtual bool is_open() const override
    { return m_intf != 0; }
  virtual bool open() override;
  virtual bool close() override;
  virtual bool scsi_pass_through(scsi_cmnd_io * iop) override;

private:
  bool open_device(io_service_t svc);
  bool open_interface(IOReturn & why);
  void drop_interface();
  void drop_device();
  bool bot(scsi_cmnd_io * iop, uint8_t & status, uint32_t & residue);
  void reset_recovery();

  usbcap::usb_id m_id;
  bool m_resolved;
  usbcap::devif_t m_dev;
  usbcap::intf_t m_intf;
  UInt8 m_in, m_out, m_ifnum;
  uint32_t m_tag;
};

darwin_usbcap_device::darwin_usbcap_device(smart_interface * intf, const char * dev_name,
                                           const char * req_type)
: smart_device(intf, dev_name, "scsi", req_type),
  m_resolved(false), m_dev(0), m_intf(0), m_in(0), m_out(0), m_ifnum(0), m_tag(1)
{
  m_resolved = usbcap::resolve(dev_name, m_id);
}

darwin_usbcap_device::~darwin_usbcap_device()
{
  if (m_dev)
    darwin_usbcap_device::close();
}

bool darwin_usbcap_device::open_device(io_service_t svc)
{
  IOCFPlugInInterface ** plug = 0;
  SInt32 score = 0;
  IOReturn kr = IOCreatePlugInInterfaceForService(svc, kIOUSBDeviceUserClientTypeID,
                                                  kIOCFPlugInInterfaceID, &plug, &score);
  if (kr != kIOReturnSuccess || !plug)
    return set_err(EIO, "USB device not accessible (0x%08x)", kr);
  HRESULT hr = (*plug)->QueryInterface(plug, CFUUIDGetUUIDBytes(kIOUSBDeviceInterfaceID320),
                                      (LPVOID *)&m_dev);
  IODestroyPlugInInterface(plug);
  if (hr || !m_dev) {
    m_dev = 0;
    return set_err(EIO, "USB device interface not available");
  }
  return true;
}

void darwin_usbcap_device::drop_interface()
{
  if (m_intf) {
    (*m_intf)->USBInterfaceClose(m_intf);
    (*m_intf)->Release(m_intf);
    m_intf = 0;
  }
}

void darwin_usbcap_device::drop_device()
{
  if (m_dev) {
    (*m_dev)->USBDeviceClose(m_dev);
    (*m_dev)->Release(m_dev);
    m_dev = 0;
  }
}

// Opens the mass storage interface in Bulk-Only mode.
bool darwin_usbcap_device::open_interface(IOReturn & why)
{
  why = kIOReturnSuccess;
  IOUSBFindInterfaceRequest req;
  req.bInterfaceClass = 8; // mass storage
  req.bInterfaceSubClass = kIOUSBFindInterfaceDontCare;
  req.bInterfaceProtocol = kIOUSBFindInterfaceDontCare;
  req.bAlternateSetting = kIOUSBFindInterfaceDontCare;
  io_iterator_t it = 0;
  IOReturn kr = (*m_dev)->CreateInterfaceIterator(m_dev, &req, &it);
  if (kr != kIOReturnSuccess || !it) {
    why = kr;
    return false;
  }
  io_service_t isvc = IOIteratorNext(it);
  IOObjectRelease(it);
  if (!isvc) {
    why = kIOReturnNotFound;
    return false;
  }
  IOCFPlugInInterface ** plug = 0;
  SInt32 score = 0;
  kr = IOCreatePlugInInterfaceForService(isvc, kIOUSBInterfaceUserClientTypeID,
                                         kIOCFPlugInInterfaceID, &plug, &score);
  IOObjectRelease(isvc);
  if (kr != kIOReturnSuccess || !plug) {
    why = kr;
    return false;
  }
  HRESULT hr = (*plug)->QueryInterface(plug, CFUUIDGetUUIDBytes(kIOUSBInterfaceInterfaceID300),
                                      (LPVOID *)&m_intf);
  IODestroyPlugInInterface(plug);
  if (hr || !m_intf) {
    m_intf = 0;
    why = kIOReturnUnsupported;
    return false;
  }
  kr = (*m_intf)->USBInterfaceOpen(m_intf);
  if (kr != kIOReturnSuccess) {
    (*m_intf)->Release(m_intf);
    m_intf = 0;
    why = kr;
    return false;
  }
  (*m_intf)->GetInterfaceNumber(m_intf, &m_ifnum);

  // UAS devices: alternate setting with protocol 0x50 is Bulk-Only.
  UInt8 proto = 0, alt = 0;
  (*m_intf)->GetInterfaceProtocol(m_intf, &proto);
  (*m_intf)->GetAlternateSetting(m_intf, &alt);
  if (proto != 0x50) {
    IOUSBConfigurationDescriptorPtr cd = 0;
    int bot_alt = -1;
    if ((*m_dev)->GetConfigurationDescriptorPtr(m_dev, 0, &cd) == kIOReturnSuccess && cd) {
      const uint8_t * p = (const uint8_t *)cd;
      unsigned total = USBToHostWord(cd->wTotalLength);
      for (unsigned off = 0; off + 2 <= total && p[off] >= 2; off += p[off]) {
        if (p[off + 1] == 4 && off + 9 <= total && p[off + 2] == m_ifnum
            && p[off + 5] == 8 && p[off + 7] == 0x50) {
          bot_alt = p[off + 3];
          break;
        }
      }
    }
    if (bot_alt < 0) {
      drop_interface();
      why = kIOReturnUnsupported;
      return false;
    }
    kr = (*m_intf)->SetAlternateInterface(m_intf, (UInt8)bot_alt);
    if (kr != kIOReturnSuccess) {
      drop_interface();
      why = kr;
      return false;
    }
  }

  UInt8 eps = 0;
  (*m_intf)->GetNumEndpoints(m_intf, &eps);
  m_in = m_out = 0;
  for (UInt8 pipe = 1; pipe <= eps; pipe++) {
    UInt8 dir = 0, num = 0, type = 0, interval = 0;
    UInt16 mps = 0;
    if ((*m_intf)->GetPipeProperties(m_intf, pipe, &dir, &num, &type, &mps, &interval) != kIOReturnSuccess)
      continue;
    if (type != kUSBBulk)
      continue;
    if (dir == kUSBIn && !m_in)
      m_in = pipe;
    else if (dir == kUSBOut && !m_out)
      m_out = pipe;
  }
  if (!m_in || !m_out) {
    drop_interface();
    why = kIOReturnUnsupported;
    return false;
  }
  return true;
}

bool darwin_usbcap_device::open()
{
  if (m_intf)
    return true;
  if (!m_resolved)
    return set_err(ENODEV, "%s: not a USB drive", get_dev_name());
  if (geteuid() != 0)
    return set_err(EACCES, "Reading a USB drive this way requires administrator rights (root)");
  std::string where;
  if (usbcap::has_mounts(m_id.loc, where))
    return set_err(EBUSY, "A volume of this drive is mounted (%s) - unmount it first", where.c_str());

  io_service_t svc = usbcap::service_for_location(m_id.loc);
  if (!svc)
    return set_err(ENODEV, "USB device 0x%08x not found", m_id.loc);
  bool ok = open_device(svc);
  IOObjectRelease(svc);
  if (!ok)
    return false;
  (*m_dev)->USBDeviceOpenSeize(m_dev); // fails while the kernel driver owns it - that's fine

  IOReturn why = kIOReturnSuccess;
  if (open_interface(why))
    return true;

  // The macOS driver owns the device: take it over.
  IOReturn kr = (*m_dev)->USBDeviceReEnumerate(m_dev, kUSBReEnumerateCaptureDeviceMask);
  drop_device();
  if (kr != kIOReturnSuccess) {
    if (kr == kIOReturnNotPrivileged)
      return set_err(EACCES, "macOS refused to hand over the USB device (not privileged)");
    return set_err(EIO, "macOS refused to hand over the USB device (0x%08x)", kr);
  }

  // Re-open; the device keeps its location ID.
  for (int tries = 0; tries < 50; tries++) {
    usleep(200000);
    svc = usbcap::service_for_location(m_id.loc);
    if (!svc)
      continue;
    ok = open_device(svc);
    IOObjectRelease(svc);
    if (!ok)
      continue;
    if ((*m_dev)->USBDeviceOpenSeize(m_dev) != kIOReturnSuccess) {
      drop_device();
      continue;
    }
    UInt8 cfg = 0;
    if ((*m_dev)->GetConfiguration(m_dev, &cfg) == kIOReturnSuccess && cfg == 0) {
      IOUSBConfigurationDescriptorPtr cd = 0;
      if ((*m_dev)->GetConfigurationDescriptorPtr(m_dev, 0, &cd) == kIOReturnSuccess && cd)
        (*m_dev)->SetConfiguration(m_dev, cd->bConfigurationValue);
    }
    if (open_interface(why)) {
      (*m_intf)->ClearPipeStallBothEnds(m_intf, m_in);
      (*m_intf)->ClearPipeStallBothEnds(m_intf, m_out);
      return true;
    }
    drop_device();
  }
  // Give it back before failing.
  svc = usbcap::service_for_location(m_id.loc);
  if (svc) {
    if (open_device(svc)) {
      (*m_dev)->USBDeviceReEnumerate(m_dev, 0);
      drop_device();
    }
    IOObjectRelease(svc);
  }
  if (why == kIOReturnUnsupported)
    return set_err(ENOSYS, "USB device has no Bulk-Only mass storage interface");
  return set_err(EIO, "USB mass storage interface could not be opened (0x%08x)", why);
}

bool darwin_usbcap_device::close()
{
  drop_interface();
  if (m_dev) {
    const char * keep = getenv("WIPEKIT_USBCAP_KEEP");
    if (!(keep && *keep && strcmp(keep, "0")))
      (*m_dev)->USBDeviceReEnumerate(m_dev, 0); // hand back to macOS
    drop_device();
  }
  return true;
}

void darwin_usbcap_device::reset_recovery()
{
  if (!m_intf)
    return;
  IOUSBDevRequest r;
  memset(&r, 0, sizeof(r));
  r.bmRequestType = USBmakebmRequestType(kUSBOut, kUSBClass, kUSBInterface);
  r.bRequest = 0xff; // Bulk-Only Mass Storage Reset
  r.wValue = 0;
  r.wIndex = m_ifnum;
  r.wLength = 0;
  (*m_intf)->ControlRequest(m_intf, 0, &r);
  (*m_intf)->ClearPipeStallBothEnds(m_intf, m_in);
  (*m_intf)->ClearPipeStallBothEnds(m_intf, m_out);
}

bool darwin_usbcap_device::bot(scsi_cmnd_io * iop, uint8_t & status, uint32_t & residue)
{
  status = 0xff;
  residue = 0;
  if (iop->cmnd_len < 1 || iop->cmnd_len > 16)
    return false;
  UInt32 tmo = (iop->timeout ? iop->timeout : 60) * 1000;
  uint32_t len = (iop->dxfer_dir == DXFER_NONE ? 0 : (uint32_t)iop->dxfer_len);
  uint32_t tag = m_tag++;

  uint8_t cbw[31];
  memset(cbw, 0, sizeof(cbw));
  usbcap::put32(cbw, 0x43425355); // "USBC"
  usbcap::put32(cbw + 4, tag);
  usbcap::put32(cbw + 8, len);
  cbw[12] = (iop->dxfer_dir == DXFER_FROM_DEVICE ? 0x80 : 0x00);
  cbw[13] = 0; // LUN
  cbw[14] = (uint8_t)iop->cmnd_len;
  memcpy(cbw + 15, iop->cmnd, iop->cmnd_len);

  IOReturn kr = (*m_intf)->WritePipeTO(m_intf, m_out, cbw, sizeof(cbw), tmo, tmo);
  if (kr != kIOReturnSuccess) {
    reset_recovery();
    return false;
  }

  if (len) {
    if (iop->dxfer_dir == DXFER_FROM_DEVICE) {
      UInt32 got = len;
      kr = (*m_intf)->ReadPipeTO(m_intf, m_in, iop->dxferp, &got, tmo, tmo);
      if (kr == kIOUSBPipeStalled)
        (*m_intf)->ClearPipeStallBothEnds(m_intf, m_in);
      else if (kr != kIOReturnSuccess && kr != kIOReturnUnderrun) {
        reset_recovery();
        return false;
      }
    }
    else {
      kr = (*m_intf)->WritePipeTO(m_intf, m_out, iop->dxferp, len, tmo, tmo);
      if (kr == kIOUSBPipeStalled)
        (*m_intf)->ClearPipeStallBothEnds(m_intf, m_out);
      else if (kr != kIOReturnSuccess) {
        reset_recovery();
        return false;
      }
    }
  }

  uint8_t csw[13];
  for (int attempt = 0; ; attempt++) {
    UInt32 got = sizeof(csw);
    memset(csw, 0, sizeof(csw));
    kr = (*m_intf)->ReadPipeTO(m_intf, m_in, csw, &got, tmo, tmo);
    if ((kr == kIOUSBPipeStalled) && attempt == 0) {
      (*m_intf)->ClearPipeStallBothEnds(m_intf, m_in);
      continue;
    }
    if (kr != kIOReturnSuccess || got != sizeof(csw)) {
      reset_recovery();
      return false;
    }
    break;
  }
  if (usbcap::get32(csw) != 0x53425355 || usbcap::get32(csw + 4) != tag || csw[12] > 1) {
    reset_recovery(); // phase error or invalid status
    return false;
  }
  residue = usbcap::get32(csw + 8);
  status = csw[12];
  return true;
}

bool darwin_usbcap_device::scsi_pass_through(scsi_cmnd_io * iop)
{
  if (!m_intf)
    return set_err(EBADF, "Device not open");
  uint8_t status = 0;
  uint32_t residue = 0;
  if (!bot(iop, status, residue))
    return set_err(EIO, "USB transfer failed");
  iop->resid = (int)residue;
  iop->scsi_status = 0;
  iop->resp_sense_len = 0;
  if (status == 1) {
    iop->scsi_status = SCSI_STATUS_CHECK_CONDITION;
    if (iop->sensep && iop->max_sense_len) {
      uint8_t sense[252];
      memset(sense, 0, sizeof(sense));
      size_t want = iop->max_sense_len < sizeof(sense) ? iop->max_sense_len : sizeof(sense);
      uint8_t cdb[6] = { 0x03, 0x01 /* descriptor format */, 0, 0, (uint8_t)want, 0 };
      scsi_cmnd_io s;
      memset(&s, 0, sizeof(s));
      s.cmnd = cdb;
      s.cmnd_len = sizeof(cdb);
      s.dxfer_dir = DXFER_FROM_DEVICE;
      s.dxferp = sense;
      s.dxfer_len = want;
      s.timeout = 10;
      uint8_t st2 = 0;
      uint32_t res2 = 0;
      if (bot(&s, st2, res2) && st2 == 0) {
        size_t n = (res2 < want ? want - res2 : 0);
        memcpy(iop->sensep, sense, n);
        iop->resp_sense_len = n;
      }
    }
  }
  return true;
}
