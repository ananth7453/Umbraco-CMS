﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using umbraco.cms.businesslogic.installer;

namespace umbraco.presentation.install.steps.Definitions
{

    [Obsolete("This is no longer used and will be removed from the codebase in the future. These legacy installer step classes have been superceded by classes in the Umbraco.Web.Install.Steps namespace but are marked internal as they are not to be used in external code.")]
    public class License : InstallerStep
    {
        public override string Alias
        {
            get { return "license"; }
        }

        public override string Name
        {
            get { return "License"; }
        }



        public override string UserControl
        {
            get { return IO.SystemDirectories.Install + "/steps/license.ascx"; }
        }

        public override bool Completed()
        {
            return false;
        }


    }
}