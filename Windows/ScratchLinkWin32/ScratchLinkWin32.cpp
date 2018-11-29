#include "stdafx.h"

#include "LocalMemory.h"
#include "ScratchLinkWin32.h"

using namespace System::Diagnostics;

#pragma warning(push)
#pragma warning(disable:4483)
// Ensure compatibility with "Any CPU": we need a module static constructor
void __clrcall __identifier(".cctor")() {
}
#pragma warning(pop)

// Work around a Bluetooth security issue in Windows 10.0.15063 (1703 Creators Update)
// Without this BluetoothLEDevice.FromBluetoothAddressAsync never returns.
// For more info visit these links:
// https://stackoverflow.com/a/43673775
// https://social.msdn.microsoft.com/Forums/en-US/58da3fdb-a0e1-4161-8af3-778b6839f4e1/bluetooth-bluetoothledevicefromidasync-does-not-complete-on-10015063
void ScratchLink::Win32::RunBluetoothSecurityWorkaround()
{
	const char* const stringSecurityDescriptor = "O:BAG:BAD:(A;;0x7;;;PS)(A;;0x3;;;SY)(A;;0x7;;;BA)(A;;0x3;;;AC)(A;;0x3;;;LS)(A;;0x3;;;NS)";
	LocalMemory::unique_ptr<SECURITY_DESCRIPTOR> relativeSecurityDescriptor;

	{
		PSECURITY_DESCRIPTOR relativeSecurityDescriptorRaw = nullptr;

		if (FAILED(ConvertStringSecurityDescriptorToSecurityDescriptorA(
			stringSecurityDescriptor,
			SDDL_REVISION_1,
			&relativeSecurityDescriptorRaw,
			nullptr)))
		{
			Debug::Print("Failed to convert Bluetooth security descriptor. Bluetooth access may not work.");
			return;
		}

		relativeSecurityDescriptor.reset((SECURITY_DESCRIPTOR*)relativeSecurityDescriptorRaw);
	}

	DWORD absoluteSecurityDescriptorSize = 0;
	DWORD discretionaryAclSize = 0;
	DWORD systemAclSize = 0;
	DWORD ownerSidSize = 0;
	DWORD primaryGroupSidSize = 0;

	// Collect sizes
	MakeAbsoluteSD(
		relativeSecurityDescriptor.get(),
		nullptr, &absoluteSecurityDescriptorSize,
		nullptr, &discretionaryAclSize,
		nullptr, &systemAclSize,
		nullptr, &ownerSidSize,
		nullptr, &primaryGroupSidSize);

	const auto absoluteSecurityDescriptor = LocalMemory::make_unique<SECURITY_DESCRIPTOR>(absoluteSecurityDescriptorSize);
	const auto discretionaryAcl = LocalMemory::make_unique<ACL>(discretionaryAclSize);
	const auto systemAcl = LocalMemory::make_unique<ACL>(systemAclSize);
	const auto ownerSid = LocalMemory::make_unique<SID>(ownerSidSize);
	const auto primaryGroupSid = LocalMemory::make_unique<SID>(primaryGroupSidSize);


}
