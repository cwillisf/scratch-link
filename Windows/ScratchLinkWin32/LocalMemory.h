#pragma once

#include <memory>

#include <intsafe.h> // for DWORD


// Wrap types from <memory> using Win32 "LocalAlloc" and "LocalFree" instead of new & delete.
// For example, LocalMemory::unique_ptr<T> wraps std::unique_ptr<T>.
// Allocators require explicit size arguments.
namespace LocalMemory
{
	// Template deleter for items allocated with "LocalAlloc" -- used internally by types below.
	template<typename T>
	void deleter(T* p)
	{
		LocalFree(p);
	}

	template<typename T>
	class unique_ptr: public std::unique_ptr<T>
	{
	public:
		unique_ptr() noexcept: std::unique_ptr(nullptr, LocalMemory::deleter) {}
		unique_ptr(nullptr_t) noexcept: std::unique_ptr(nullptr, LocalMemory::deleter) {}
		unique_ptr(T* p) noexcept: std::unique_ptr(p, LocalMemory::deleter) {}
		unique_ptr(unique_ptr&& other) noexcept: std::unique_ptr(other) {}

		template<class Other_t>


		template<class Other_t>
		unique_ptr(std::auto_ptr<Other_t>&& other) noexcept: std::unique_ptr(other, LocalMemory::deleter) {}
	};

	template<typename T>
	using shared_ptr = std::shared_ptr<T, decltype(&LocalMemory::deleter<T>)>;

	template<typename T>
	LocalMemory::unique_ptr<T> make_unique(DWORD size)
	{
		T* p = LocalAlloc(0, size);
		return LocalMemory::unique_ptr<T>(p);
	}

	template<typename T>
	LocalMemory::shared_ptr<T> make_shared(DWORD size)
	{
		T* p = LocalAlloc(0, size);
		return LocalMemory::shared_ptr<T>(p);
	}
}
