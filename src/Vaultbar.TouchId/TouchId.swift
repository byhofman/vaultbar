// Secure Enclave access for Vaultbar. CryptoKit has no C API, so .NET reaches it through this small library.
//
// CryptoKit is used because its key blob lives outside the keychain: the data-protection keychain needs an entitlement
// that an ad-hoc signed app cannot have, and the blob stays valid when an update re-signs the app.
import CryptoKit
import Foundation
import LocalAuthentication

private let ok: Int32 = 0
private let unexpected: Int32 = -1

private func status(of error: Error) -> Int32 {
    let code = (error as NSError).code
    return code == 0 ? unexpected : Int32(truncatingIfNeeded: code)
}

@_cdecl("vaultbar_touchid_available")
public func available() -> Int32 {
    var error: NSError?
    let canAsk = LAContext().canEvaluatePolicy(.deviceOwnerAuthenticationWithBiometrics, error: &error)
    return SecureEnclave.isAvailable && canAsk ? 1 : 0
}

/// Creates a key only the Secure Enclave can use, and only after Touch ID for the fingers enrolled right now.
@_cdecl("vaultbar_touchid_create")
public func create(
    _ blob: UnsafeMutablePointer<UInt8>, _ capacity: Int32, _ written: UnsafeMutablePointer<Int32>
) -> Int32 {
    var failure: Unmanaged<CFError>?
    guard let access = SecAccessControlCreateWithFlags(
        nil, kSecAttrAccessibleWhenUnlockedThisDeviceOnly, [.privateKeyUsage, .biometryCurrentSet], &failure)
    else { return failure.map { status(of: $0.takeRetainedValue()) } ?? unexpected }
    do {
        let key = try SecureEnclave.P256.KeyAgreement.PrivateKey(accessControl: access)
        let data = key.dataRepresentation
        guard data.count <= Int(capacity) else { return unexpected }
        data.copyBytes(to: blob, count: data.count)
        written.pointee = Int32(data.count)
        return ok
    } catch {
        return status(of: error)
    }
}

/// Turns the challenge into a P-256 public key and agrees on a secret with the enclave key, which asks for Touch ID.
/// The same key and challenge always give the same 32 bytes, and nothing outside the enclave can compute them.
@_cdecl("vaultbar_touchid_derive")
public func derive(
    _ blob: UnsafePointer<UInt8>, _ blobLength: Int32,
    _ challenge: UnsafePointer<UInt8>, _ challengeLength: Int32,
    _ reason: UnsafePointer<CChar>, _ noPrompt: Int32,
    _ secret: UnsafeMutablePointer<UInt8>
) -> Int32 {
    do {
        let context = LAContext()
        context.localizedReason = String(cString: reason)
        context.interactionNotAllowed = noPrompt != 0
        let key = try SecureEnclave.P256.KeyAgreement.PrivateKey(
            dataRepresentation: Data(bytes: blob, count: Int(blobLength)), authenticationContext: context)
        let scalar = SHA256.hash(data: Data(bytes: challenge, count: Int(challengeLength)))
        let peer = try P256.KeyAgreement.PrivateKey(rawRepresentation: Data(scalar))
        let shared = try key.sharedSecretFromKeyAgreement(with: peer.publicKey)
        shared.withUnsafeBytes { secret.update(from: $0.bindMemory(to: UInt8.self).baseAddress!, count: 32) }
        return ok
    } catch {
        return status(of: error)
    }
}

// The data-protection keychain, once Vaultbar is signed with a Developer ID and a provisioning profile that grant it a
// keychain access group (M3, VBR-26). A key there is usable only by apps signed into that group, so another process
// running as the same user can no longer load it, as it can a key blob from a file.

/// 1 when this process holds a keychain access group, which only a signed build with its profile does.
@_cdecl("vaultbar_touchid_keychain_available")
public func keychainAvailable() -> Int32 {
    guard let task = SecTaskCreateFromSelf(nil) else { return 0 }
    let groups = SecTaskCopyValueForEntitlement(task, "keychain-access-groups" as CFString, nil)
    return (groups as? [String])?.isEmpty == false ? 1 : 0
}

private func keyQuery(_ tag: UnsafePointer<CChar>) -> [String: Any] {
    [
        kSecClass as String: kSecClassKey,
        kSecAttrApplicationTag as String: Data(String(cString: tag).utf8),
        kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom,
        kSecUseDataProtectionKeychain as String: true,
    ]
}

/// The same key as vaultbar_touchid_create, kept in the keychain under the tag instead of returned as a blob.
@_cdecl("vaultbar_touchid_keychain_create")
public func keychainCreate(_ tag: UnsafePointer<CChar>) -> Int32 {
    var failure: Unmanaged<CFError>?
    guard let access = SecAccessControlCreateWithFlags(
        nil, kSecAttrAccessibleWhenUnlockedThisDeviceOnly, [.privateKeyUsage, .biometryCurrentSet], &failure)
    else { return failure.map { status(of: $0.takeRetainedValue()) } ?? unexpected }
    SecItemDelete(keyQuery(tag) as CFDictionary)
    let attributes: [String: Any] = [
        kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom,
        kSecAttrKeySizeInBits as String: 256,
        kSecAttrTokenID as String: kSecAttrTokenIDSecureEnclave,
        kSecUseDataProtectionKeychain as String: true,
        kSecPrivateKeyAttrs as String: [
            kSecAttrIsPermanent as String: true,
            kSecAttrApplicationTag as String: Data(String(cString: tag).utf8),
            kSecAttrAccessControl as String: access,
        ] as [String: Any],
    ]
    guard SecKeyCreateRandomKey(attributes as CFDictionary, &failure) != nil else {
        return failure.map { status(of: $0.takeRetainedValue()) } ?? unexpected
    }
    return ok
}

/// vaultbar_touchid_derive for the keychain key: the same key agreement, so the same 32 bytes for the same challenge.
@_cdecl("vaultbar_touchid_keychain_derive")
public func keychainDerive(
    _ tag: UnsafePointer<CChar>,
    _ challenge: UnsafePointer<UInt8>, _ challengeLength: Int32,
    _ reason: UnsafePointer<CChar>, _ noPrompt: Int32,
    _ secret: UnsafeMutablePointer<UInt8>
) -> Int32 {
    let context = LAContext()
    context.localizedReason = String(cString: reason)
    context.interactionNotAllowed = noPrompt != 0
    var query = keyQuery(tag)
    query[kSecAttrKeyClass as String] = kSecAttrKeyClassPrivate
    query[kSecReturnRef as String] = true
    query[kSecUseAuthenticationContext as String] = context
    var item: CFTypeRef?
    let found = SecItemCopyMatching(query as CFDictionary, &item)
    guard found == errSecSuccess, let item else { return found == errSecSuccess ? unexpected : found }
    let key = item as! SecKey
    do {
        let scalar = SHA256.hash(data: Data(bytes: challenge, count: Int(challengeLength)))
        let peer = try P256.KeyAgreement.PrivateKey(rawRepresentation: Data(scalar))
        guard let peerKey = SecKeyCreateWithData(
            peer.publicKey.x963Representation as CFData,
            [kSecAttrKeyType: kSecAttrKeyTypeECSECPrimeRandom, kSecAttrKeyClass: kSecAttrKeyClassPublic] as CFDictionary, nil)
        else { return unexpected }
        var failure: Unmanaged<CFError>?
        guard let shared = SecKeyCopyKeyExchangeResult(key, .ecdhKeyExchangeStandard, peerKey, [:] as CFDictionary, &failure) as Data?,
              shared.count == 32
        else { return failure.map { status(of: $0.takeRetainedValue()) } ?? unexpected }
        shared.withUnsafeBytes { secret.update(from: $0.bindMemory(to: UInt8.self).baseAddress!, count: 32) }
        return ok
    } catch {
        return status(of: error)
    }
}

@_cdecl("vaultbar_touchid_keychain_delete")
public func keychainDelete(_ tag: UnsafePointer<CChar>) -> Int32 {
    let result = SecItemDelete(keyQuery(tag) as CFDictionary)
    return result == errSecItemNotFound ? ok : result
}
