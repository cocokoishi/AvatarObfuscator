using UnityEngine;

namespace FuckRipper.AvatarObfuscator
{
    /// <summary>
    /// Drop this on the avatar root (the GameObject that has VRCAvatarDescriptor).
    /// At upload / Play time, NDMF clones the avatar and our plugin obfuscates the clone.
    /// The original avatar in the scene is never modified, so removing this component
    /// fully reverts the change — there is nothing to revert.
    /// </summary>
    [AddComponentMenu("Cocokoishi/Avatar Obfuscator")]
    [DisallowMultipleComponent]
    [HelpURL("https://space.bilibili.com/5145514")]
    public sealed class AvatarObfuscator : MonoBehaviour
#if FR_OBF_VRCSDK3_AVATARS
        , VRC.SDKBase.IEditorOnly
#endif
    {
        [SerializeField]
        public ObfuscationOptions options = new ObfuscationOptions();
    }
}
