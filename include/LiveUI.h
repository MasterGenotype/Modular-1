#pragma once
#include <string>

class LiveUI {
public:
    void begin();

    // Set the top-line label and its total units (files, mods, renames, etc.)
    void setOperation(const std::string& op, int total);

    // Update the second line (single changing status line)
    void setStatus(const std::string& s);

    // Set absolute progress value for the operation
    void setProgress(int current);

    // Increment progress by delta
    void tick(int delta = 1);

    // Mark complete and leave the cursor below UI
    void finish(const std::string& finalStatus = "");

private:
    void redraw();
    void clearLine();
    std::string bar(int current, int total, int width = 50);

    std::string op_ = "Idle";
    std::string status_;
    int total_ = 0;
    int current_ = 0;
};
