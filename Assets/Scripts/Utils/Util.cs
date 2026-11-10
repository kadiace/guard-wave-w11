using UnityEngine.InputSystem;

public static class Util
{
    public static string RemoveUnityCloneSuffix(string name)
    {
        const string suffix = "(Clone)";
        return name.EndsWith(suffix)
            ? name[..^suffix.Length]
            : name;
    }

    private readonly static string[] _bindingActions =
    {
        "Move",
        "Look",
        "Jump",
        "Sprint",
        "Interact",
        "Restart",
        "Pause",
        "BlackHole",
        "WhiteHole",
        "Retrieve",
        "Confirm"
    };

    public static string ReplaceBindingName(string text)
    {
        foreach (string action in _bindingActions)
        {
            text = text.Replace(
                $"{{{action}}}",
                GetBindingName(action)
            );
        }

        return text;
    }

    public static string GetBindingName(string actionName)
    {
        InputAction action = InputSystem.actions.FindAction(actionName);

        if (action == null)
            return actionName;

        bool isGamepad = Managers.Input.GamePadConnected;

        for (int i = 0; i < action.bindings.Count; i++)
        {
            InputBinding binding = action.bindings[i];

            if (binding.isComposite)
            {
                bool compositeIsGamepad = false;

                for (int j = i + 1; j < action.bindings.Count; j++)
                {
                    InputBinding part = action.bindings[j];

                    if (!part.isPartOfComposite)
                        break;

                    string path = part.effectivePath;

                    if (!string.IsNullOrEmpty(path) &&
                        path.StartsWith("<Gamepad>/"))
                    {
                        compositeIsGamepad = true;
                        break;
                    }

                    if (part.groups != null &&
                        part.groups.Contains("Gamepad"))
                    {
                        compositeIsGamepad = true;
                        break;
                    }
                }

                if (isGamepad == compositeIsGamepad)
                    return action.GetBindingDisplayString(i);
            }
            else if (!binding.isPartOfComposite)
            {
                string path = binding.effectivePath;

                bool isBindingGamepad =
                    (binding.groups != null &&
                     binding.groups.Contains("Gamepad")) ||
                    (!string.IsNullOrEmpty(path) &&
                     path.StartsWith("<Gamepad>/"));

                if (isGamepad == isBindingGamepad)
                    return action.GetBindingDisplayString(i);
            }
        }

        return action.GetBindingDisplayString();
    }
}
