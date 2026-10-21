namespace ArdUi;

// Decisions that grant access, or open a way in, are for the person sitting at this machine. When this PC is being controlled with FRD, the
// controller's clicks and keys are injected input, and these controls refuse them (see LocalInput). It is a guard against a controller
// approving itself, not a lock against someone who owns the desktop: such a person can still end ArdUi or edit its files.
sealed partial class MainWindow
{
    bool Person(string what)
    {
        if (LocalInput.ActivationByPerson) return true;
        Toast("“" + what + "”需要被控端的人亲自操作：远程输入不能执行这个操作。");
        return false;
    }
}
